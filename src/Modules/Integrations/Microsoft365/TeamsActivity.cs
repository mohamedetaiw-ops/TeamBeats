using Microsoft.Extensions.Configuration;
using TeamBeats.Entitlements.Contracts;
using TeamBeats.Modules.Identity.Contracts;
using TeamBeats.Modules.Integrations.Contracts;
using TeamBeats.Modules.Integrations.Runtime;
using TeamBeats.Persistence;

namespace TeamBeats.Modules.Integrations.Microsoft365;

/// <summary>
/// Teams activity notifications (spec 015 US5) through the tenant's Microsoft 365 connection with the Teams feature
/// granted (application permission <c>TeamsActivity.Send</c> only). The person is found by the Entra object ID directory
/// sync linked them under, never by email. The notification opens the link in the TeamBeats app inside Teams when the
/// platform has a Teams app ID (<c>Integrations:Microsoft365:TeamsAppId</c>), otherwise in the browser.
/// </summary>
public sealed class TeamsActivity(IDbSession session, IEntitlementService entitlements, IMicrosoftGraph graph, IProvisioningTarget identities,
    IDirectorySource directory, TenantLinks links, IConfiguration configuration, BotFramework bot) : ITeamsMessaging
{
    /// <summary>Teams shows at most 150 characters of preview text.</summary>
    public const int MaxPreview = 150;

    private async Task<string?> DirectoryAsync(CancellationToken ct)
    {
        if (!await entitlements.IsEnabledAsync(Capabilities.NotificationsTeams, ct))
        {
            return null;
        }
        return await session.ScalarAsync<string?>(
            """
            select c.external_tenant_id from integrations.connection c
            join integrations.connection_feature f on f.connection_id = c.id
            where c.kind = @kind and c.enabled and f.feature = @feature and f.enabled and f.consent_status = 'granted'
            order by c.created_at, c.id limit 1
            """, new { kind = ConnectionKinds.Microsoft365, feature = Microsoft365Features.Teams }, ct);
    }

    public async Task<bool> AvailableAsync(CancellationToken ct = default) => await DirectoryAsync(ct) is not null;

    public async Task<TeamsSendOutcome> SendActivityAsync(Guid userId, string title, string text, string link, CancellationToken ct = default)
    {
        if (await DirectoryAsync(ct) is not { } external)
        {
            return TeamsSendOutcome.Unavailable;
        }
        if (await identities.ExternalIdAsync(userId, directory.IssuerFor(external), ct) is not { } objectId || !Guid.TryParse(objectId, out _))
        {
            return TeamsSendOutcome.NotLinked;
        }
        var url = await links.AbsoluteAsync(link, ct);
        var body = new
        {
            topic = new { source = "text", value = Fit(title, 120), webUrl = DeepLink(url) },
            activityType = "systemDefault",
            previewText = new { content = Fit(text, MaxPreview) },
            templateParameters = new[] { new { name = "systemDefault", value = Fit(text, MaxPreview) } },
        };
        try
        {
            var token = await graph.AcquireAppTokenAsync(external, ct);
            await graph.AppPostAsync(token, $"users/{objectId}/teamwork/sendActivityNotification", body, ct);
            return TeamsSendOutcome.Sent;
        }
        catch (GraphCallException ex) when (ex.Kind is "not_found" or "forbidden")
        {
            return TeamsSendOutcome.NotInstalled;
        }
        catch (GraphConsentMissingException)
        {
            return TeamsSendOutcome.Unavailable;
        }
        catch (GraphUnavailableException)
        {
            return TeamsSendOutcome.Transient;
        }
        catch (GraphCallException)
        {
            return TeamsSendOutcome.NotInstalled;
        }
    }

    public async Task<TeamsSendOutcome> SendCardAsync(Guid userId, TeamsCard card, CancellationToken ct = default)
    {
        if (!bot.Enabled || await DirectoryAsync(ct) is not { } external)
        {
            return TeamsSendOutcome.Unavailable;
        }
        if (await identities.ExternalIdAsync(userId, directory.IssuerFor(external), ct) is not { } objectId || !Guid.TryParse(objectId, out _))
        {
            return TeamsSendOutcome.NotLinked;
        }
        var url = DeepLink(await links.AbsoluteAsync(card.Link, ct));
        var fitted = card with { Title = Fit(card.Title, 120), Text = Fit(card.Text, 400) };
        return await bot.SendAsync(external, objectId, BotFramework.Card(fitted, url, card.Rtl), fitted.Title, ct);
    }

    public async Task<TeamsSendOutcome> ReplyAsync(Guid userId, TeamsConversation conversation, TeamsCard card, CancellationToken ct = default)
    {
        if (!bot.Enabled || await DirectoryAsync(ct) is null)
        {
            return TeamsSendOutcome.Unavailable;
        }
        var url = DeepLink(await links.AbsoluteAsync(card.Link, ct));
        var extra = new List<(string, string)>();
        foreach (var l in (card.Links ?? []).Take(5))
        {
            extra.Add((Fit(l.Label, 60), DeepLink(await links.AbsoluteAsync(l.Link, ct))));
        }
        var fitted = card with { Title = Fit(card.Title, 120), Text = Fit(card.Text, 4000) };
        return await bot.ReplyAsync(conversation, BotFramework.Card(fitted, url, card.Rtl, extra), fitted.Title, ct);
    }

    public async Task<bool> IsPersonAsync(Guid userId, string entraTenantId, string aadObjectId, CancellationToken ct = default) =>
        await DirectoryAsync(ct) is { } external && Guid.TryParse(external, out var mine) && Guid.TryParse(entraTenantId, out var caller) && mine == caller
        && await identities.ExternalIdAsync(userId, directory.IssuerFor(external), ct) is { } objectId
        && Guid.TryParse(objectId, out var linked) && Guid.TryParse(aadObjectId, out var presented) && linked == presented;

    /// <summary>A Teams deep link to the TeamBeats app showing the page, or the page itself when no Teams app is registered.</summary>
    private string DeepLink(string url) =>
        configuration["Integrations:Microsoft365:TeamsAppId"] is { Length: > 0 } app && Guid.TryParse(app, out var appId)
            ? $"https://teams.microsoft.com/l/entity/{appId:D}/teambeats?webUrl={Uri.EscapeDataString(url)}&label=TeamBeats"
            : url;

    private static string Fit(string text, int max) => text.Length > max ? text[..(max - 1)] + "…" : text;
}
