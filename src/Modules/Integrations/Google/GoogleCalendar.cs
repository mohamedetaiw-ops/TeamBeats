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
/// Google Calendar meetings of the signed-in employee (spec 037 FR-012), the Google adapter behind the calendar ports of
/// spec 018 FR-024. It acts only as the employee's own address through the connection's domain-wide delegation, with the
/// read-only events scope, and reads only their primary calendar, so it never sees another person's calendar. Nothing is
/// stored: answers are cached in memory for 5 minutes per tenant and user, and the last good answer for an hour so an
/// outage can show it with its age. No Google type leaves this class.
/// </summary>
public sealed class GoogleCalendar(
    IDbSession session,
    ITenantContext context,
    IEntitlementService entitlements,
    IUserDirectory users,
    GoogleApi api,
    GoogleSignIn signIn,
    IMemoryCache cache,
    IClock clock)
{
    public static readonly TimeSpan Freshness = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan LastKnown = TimeSpan.FromHours(1);
    public const int MaxResults = 250;

    private sealed record Entry(IReadOnlyList<CalendarOccurrence> Items, DateTimeOffset AsOf);

    /// <summary>Null when the tenant has no enabled Google Workspace connection with the calendar feature.</summary>
    public async Task<MeetingSourceResult?> ReadAsync(CalendarQuery query, CancellationToken ct)
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
            loaded = await api.LoadAsync(id, GoogleFeatures.Calendar, ct);
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
            // Only people of the customer's Google domain have a Google calendar to read.
            return new MeetingSourceResult(CalendarSources.Google, M365States.NoMailbox, null, []);
        }
        var from = query.From.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
        var to = query.To.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
        var key = $"google-calendar:{context.RequireTenant():N}:{me:N}:{from}:{to}";
        if (!query.Refresh && cache.Get<Entry>(key + ":fresh") is { } fresh)
        {
            return new MeetingSourceResult(CalendarSources.Google, M365States.Ok, fresh.AsOf, fresh.Items);
        }
        var token = await signIn.TokenAsync(found.Connection, found.Settings, email, GoogleHosts.CalendarScope, ct);
        string? error = token.ErrorClass;
        JsonElement body = default;
        if (token.Ok)
        {
            var url = $"{GoogleHosts.CalendarBase}/calendars/primary/events?singleEvents=true&orderBy=startTime&maxResults={MaxResults}"
                + $"&timeMin={Uri.EscapeDataString(from)}&timeMax={Uri.EscapeDataString(to)}"
                + "&fields=items(id,iCalUID,summary,start,end,status,transparency,visibility,location,hangoutLink,htmlLink,attendees(self,responseStatus),conferenceData(entryPoints(entryPointType,uri)))";
            (body, error, _) = await api.GetAsync(token.AccessToken!, url, ct);
        }
        if (error is null)
        {
            var items = GoogleApi.Items(body, "items").Select(Map).Where(o => o is not null).Select(o => o!).ToList();
            var entry = new Entry(items, clock.UtcNow);
            cache.Set(key + ":fresh", entry, Freshness);
            cache.Set(key + ":last", entry, LastKnown);
            return new MeetingSourceResult(CalendarSources.Google, M365States.Ok, entry.AsOf, items);
        }
        if (error == ConnectorErrorClasses.CredentialRefused || error == ConnectorErrorClasses.CredentialMissing)
        {
            return new MeetingSourceResult(CalendarSources.Google, M365States.AdminSetup, null, []);
        }
        var last = cache.Get<Entry>(key + ":last");
        return new MeetingSourceResult(CalendarSources.Google, M365States.Degraded, last?.AsOf, last?.Items ?? []);
    }

    /// <summary>One Google event as a canonical occurrence; null for an event without a start.</summary>
    public static CalendarOccurrence? Map(JsonElement e)
    {
        if (GoogleApi.Text(e, "id") is not { } id || !e.TryGetProperty("start", out var start) || !e.TryGetProperty("end", out var end))
        {
            return null;
        }
        var allDay = GoogleApi.Text(start, "date") is not null;
        if (Time(start) is not { } s || Time(end) is not { } f)
        {
            return null;
        }
        var response = "none";
        if (e.TryGetProperty("attendees", out var attendees) && attendees.ValueKind == JsonValueKind.Array)
        {
            foreach (var a in attendees.EnumerateArray())
            {
                if (GoogleApi.True(a, "self"))
                {
                    response = GoogleApi.Text(a, "responseStatus") switch
                    {
                        "accepted" => "accepted",
                        "declined" => "declined",
                        "tentative" => "tentative",
                        "needsAction" => "not_responded",
                        _ => "none",
                    };
                }
            }
        }
        var join = GoogleApi.Text(e, "hangoutLink");
        if (join is null && e.TryGetProperty("conferenceData", out var conference) && conference.TryGetProperty("entryPoints", out var points)
            && points.ValueKind == JsonValueKind.Array)
        {
            join = points.EnumerateArray().Where(p => GoogleApi.Text(p, "entryPointType") == "video").Select(p => GoogleApi.Text(p, "uri")).FirstOrDefault();
        }
        if (join is not null && !join.StartsWith("https://", StringComparison.Ordinal))
        {
            join = null;
        }
        var location = GoogleApi.Text(e, "location");
        return new CalendarOccurrence(id, GoogleApi.Text(e, "summary"), s, f, allDay,
            GoogleApi.Text(e, "transparency") == "transparent" ? "free" : response == "tentative" ? "tentative" : "busy",
            GoogleApi.Text(e, "visibility") is "private" or "confidential",
            GoogleApi.Text(e, "status") == "cancelled",
            response,
            string.IsNullOrWhiteSpace(location) ? null : location,
            join,
            GoogleApi.Text(e, "htmlLink") is { } link && link.StartsWith("https://", StringComparison.Ordinal) ? link : string.Empty,
            GoogleApi.Text(e, "iCalUID"),
            CalendarSources.Google);
    }

    private static DateTimeOffset? Time(JsonElement value)
    {
        if (GoogleApi.Text(value, "dateTime") is { } dateTime
            && DateTimeOffset.TryParse(dateTime, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var instant))
        {
            return instant.ToUniversalTime();
        }
        if (GoogleApi.Text(value, "date") is { } date && DateOnly.TryParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day))
        {
            // All-day items keep their date, as Outlook gives them: midnight with no offset.
            return new DateTimeOffset(day.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        }
        return null;
    }
}

/// <summary>Outlook and Google behind one call (spec 037 FR-011): each source the tenant has, with its own state.</summary>
public sealed class MeetingCalendars(IEntitlementService entitlements, ICalendar outlook, GoogleCalendar google) : IMeetingCalendars
{
    public async Task<IReadOnlyList<MeetingSourceResult>> ReadAsync(CalendarQuery query, CancellationToken ct = default)
    {
        var list = new List<MeetingSourceResult>();
        if (await entitlements.IsEnabledAsync(Capabilities.IntegrationMicrosoft365, ct))
        {
            var result = await outlook.GetEventsAsync(query, ct);
            list.Add(new MeetingSourceResult(CalendarSources.Outlook, result.State, result.AsOf, result.Occurrences));
        }
        if (await google.ReadAsync(query, ct) is { } fromGoogle)
        {
            list.Add(fromGoogle);
        }
        return list;
    }
}
