using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;
using TeamBeats.Entitlements.Contracts;
using TeamBeats.Modules.Identity.Contracts;
using TeamBeats.Modules.Integrations.Contracts;
using TeamBeats.Modules.Integrations.Microsoft365;
using TeamBeats.Modules.Integrations.Runtime;
using TeamBeats.Persistence;
using TeamBeats.Primitives;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.Integrations.Google;

/// <summary>
/// Gmail behind <see cref="IMailbox"/> (spec 018 R3, FR-024): the signed-in employee's own Inbox, read as their own address
/// through the connection's domain-wide delegation with the metadata scope, so Gmail gives headers and labels and never a
/// body or preview. "Focused" is Gmail's Primary category. Nothing is stored: answers are cached in memory for 2 minutes
/// per tenant and user, and the last good answer for an hour so an outage can show it with its age.
/// </summary>
public sealed class GoogleMail(
    IDbSession session,
    ITenantContext context,
    IEntitlementService entitlements,
    IUserDirectory users,
    GoogleApi api,
    GoogleSignIn signIn,
    IMemoryCache cache,
    IClock clock)
{
    public const string ComposeLink = "https://mail.google.com/mail/?view=cm";

    private sealed record Entry(int Unread, IReadOnlyList<MailMessage> Messages, DateTimeOffset AsOf);

    /// <summary>Null when the tenant has no enabled Google Workspace connection with the mail feature.</summary>
    public async Task<MailSummary?> ReadAsync(MailQuery query, CancellationToken ct)
    {
        if (!await entitlements.IsEnabledAsync(Capabilities.IntegrationConnectors, ct))
        {
            return null;
        }
        var ids = await session.QueryAsync("select id from integrations.connection where kind = @kind and enabled order by created_at",
            new { kind = GooglePack.KindName }, r => r.GetGuid(0), ct);
        (PackConnection Connection, GoogleSettings Settings)? loaded = null;
        foreach (var id in ids)
        {
            loaded = await api.LoadAsync(id, GoogleFeatures.Mail, ct);
            if (loaded is not null)
            {
                break;
            }
        }
        if (loaded is not { } found)
        {
            return null;
        }
        var me = context.RequireUser();
        var email = (await users.GetAsync(me, ct))?.Email?.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(email) || found.Settings.Domain is not { } domain || !email.EndsWith("@" + domain, StringComparison.Ordinal))
        {
            // Only people of the customer's Google domain have a Gmail Inbox to read.
            return Summary(M365States.NoMailbox, null);
        }
        var top = Math.Clamp(query.Top, 1, 10);
        var focused = query.View == "focused";
        var key = $"google-mail:{context.RequireTenant():N}:{me:N}:{(focused ? "focused" : "all")}:{top}";
        if (!query.Refresh && cache.Get<Entry>(key + ":fresh") is { } fresh)
        {
            return Summary(M365States.Ok, fresh);
        }
        var token = await signIn.TokenAsync(found.Connection, found.Settings, email, GoogleHosts.GmailScope, ct);
        string? error = token.ErrorClass;
        Entry? entry = null;
        if (token.Ok)
        {
            (entry, error) = await FetchAsync(token.AccessToken!, top, focused, ct);
        }
        if (error is null && entry is not null)
        {
            cache.Set(key + ":fresh", entry, GraphReadCache.MailFreshness);
            cache.Set(key + ":last", entry, GraphReadCache.LastKnown);
            return Summary(M365States.Ok, entry);
        }
        if (error == ConnectorErrorClasses.CredentialRefused || error == ConnectorErrorClasses.CredentialMissing)
        {
            return Summary(M365States.AdminSetup, null);
        }
        return cache.Get<Entry>(key + ":last") is { } last ? Summary(M365States.Degraded, last) : Summary(M365States.Degraded, null);
    }

    private static MailSummary Summary(string state, Entry? entry) =>
        entry is null
            ? new MailSummary(state, null, null, [], null, CalendarSources.Google)
            : new MailSummary(state, entry.AsOf, entry.Unread, entry.Messages, ComposeLink, CalendarSources.Google);

    private async Task<(Entry?, string?)> FetchAsync(string token, int top, bool focused, CancellationToken ct)
    {
        var (label, error, _) = await api.GetAsync(token, $"{GoogleHosts.GmailBase}/users/me/labels/INBOX", ct);
        if (error is not null)
        {
            return (null, error);
        }
        var list = $"{GoogleHosts.GmailBase}/users/me/messages?labelIds=INBOX&maxResults={top}" + (focused ? "&q=" + Uri.EscapeDataString("category:primary") : string.Empty);
        (var body, error, _) = await api.GetAsync(token, list, ct);
        if (error is not null)
        {
            return (null, error);
        }
        var messages = new List<MailMessage>();
        foreach (var id in GoogleApi.Items(body, "messages").Select(m => GoogleApi.Text(m, "id")).OfType<string>().Take(top))
        {
            var url = $"{GoogleHosts.GmailBase}/users/me/messages/{Uri.EscapeDataString(id)}?format=metadata&metadataHeaders=From&metadataHeaders=Subject"
                + "&fields=id,labelIds,internalDate,payload(mimeType,headers)";
            (var message, error, _) = await api.GetAsync(token, url, ct);
            if (error is not null)
            {
                return (null, error);
            }
            if (Map(message) is { } mapped)
            {
                messages.Add(mapped);
            }
        }
        var unread = label.ValueKind == JsonValueKind.Object && label.TryGetProperty("messagesUnread", out var u) && u.TryGetInt32(out var n) ? n : 0;
        return (new Entry(unread, messages.OrderByDescending(m => m.ReceivedAt).ToList(), clock.UtcNow), null);
    }

    /// <summary>One Gmail message as a canonical message; null without an ID or a time.</summary>
    public static MailMessage? Map(JsonElement m)
    {
        if (GoogleApi.Text(m, "id") is not { } id
            || !long.TryParse(GoogleApi.Text(m, "internalDate"), NumberStyles.None, CultureInfo.InvariantCulture, out var ms))
        {
            return null;
        }
        var labels = m.TryGetProperty("labelIds", out var l) && l.ValueKind == JsonValueKind.Array
            ? l.EnumerateArray().Select(x => x.GetString()).OfType<string>().ToHashSet(StringComparer.Ordinal)
            : [];
        string? Header(string name) => m.TryGetProperty("payload", out var p) && p.TryGetProperty("headers", out var h) && h.ValueKind == JsonValueKind.Array
            ? h.EnumerateArray().Where(x => string.Equals(GoogleApi.Text(x, "name"), name, StringComparison.OrdinalIgnoreCase)).Select(x => GoogleApi.Text(x, "value")).FirstOrDefault()
            : null;
        var (sender, address) = From(Header("From"));
        var mime = m.TryGetProperty("payload", out var payload) ? GoogleApi.Text(payload, "mimeType") : null;
        return new MailMessage(id, sender, address, GraphMailbox.PlainText(Header("Subject")) ?? string.Empty,
            DateTimeOffset.FromUnixTimeMilliseconds(ms), !labels.Contains("UNREAD"), labels.Contains("IMPORTANT") ? "high" : "normal",
            mime == "multipart/mixed", null, $"https://mail.google.com/mail/u/0/#inbox/{Uri.EscapeDataString(id)}");
    }

    /// <summary><c>Name &lt;address&gt;</c>, <c>"Name" &lt;address&gt;</c> or a bare address.</summary>
    public static (string Name, string? Address) From(string? header)
    {
        if (string.IsNullOrWhiteSpace(header))
        {
            return (string.Empty, null);
        }
        var open = header.LastIndexOf('<');
        var close = header.LastIndexOf('>');
        if (open >= 0 && close > open)
        {
            var address = header[(open + 1)..close].Trim();
            var name = header[..open].Trim().Trim('"').Trim();
            return (GraphMailbox.PlainText(name) ?? address, address);
        }
        var bare = header.Trim();
        return (bare, bare.Contains('@') ? bare : null);
    }
}

/// <summary>
/// Outlook and Gmail behind <see cref="IMailbox"/> (spec 018 FR-024): Outlook while the tenant has Microsoft 365 and its
/// Mail feature on, otherwise Gmail while the Google Workspace connection has its mail feature on. The answer says which
/// source it came from.
/// </summary>
public sealed class Mailboxes(IEntitlementService entitlements, GraphMailbox outlook, GoogleMail google) : IMailbox
{
    public async Task<MailSummary> GetInboxAsync(MailQuery query, CancellationToken ct = default)
    {
        MailSummary? fromOutlook = null;
        if (await entitlements.IsEnabledAsync(Capabilities.IntegrationMicrosoft365, ct))
        {
            fromOutlook = await outlook.GetInboxAsync(query, ct);
            if (fromOutlook.State is not (M365States.TurnedOff or M365States.NotInPlan))
            {
                return fromOutlook;
            }
        }
        return await google.ReadAsync(query, ct) ?? fromOutlook ?? new MailSummary(M365States.NotInPlan, null, null, [], null);
    }
}
