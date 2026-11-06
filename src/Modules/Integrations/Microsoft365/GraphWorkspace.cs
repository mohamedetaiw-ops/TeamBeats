using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;
using TeamBeats.Modules.Integrations.Contracts;
using TeamBeats.Primitives;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.Integrations.Microsoft365;

/// <summary>
/// Short in-memory caches for Graph reads (spec 018 plan "Caching and limits"). Keys carry tenant, user and session, so
/// one employee's data never answers another. Nothing is written to a database. The last good answer of the session is
/// kept for an hour so a Graph failure can show it, with its age, as degraded (FR-011).
/// </summary>
public sealed class GraphReadCache(IMemoryCache cache, TenantContextAccessor context, IClock clock)
{
    public static readonly TimeSpan MailFreshness = TimeSpan.FromMinutes(2);
    public static readonly TimeSpan CalendarFreshness = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan LastKnown = TimeSpan.FromHours(1);

    /// <summary>Graph refreshes an employee may force per minute; beyond it a refresh answers from the cache.</summary>
    public const int RefreshesPerMinute = 6;

    private sealed record Entry<T>(T Value, DateTimeOffset AsOf);

    /// <summary>
    /// Reads through the cache. Returns the value, when it was read, and the failure kind when Graph failed and no
    /// last-known value exists for this session.
    /// </summary>
    public async Task<(T? Value, DateTimeOffset? AsOf, string State)> ReadAsync<T>(string feature, string query, TimeSpan freshness, bool refresh,
        Func<Task<T>> fetch) where T : class
    {
        var key = Key(feature, query);
        var fresh = cache.Get<Entry<T>>(key + ":fresh");
        if (fresh is not null && (!refresh || !AllowRefresh()))
        {
            return (fresh.Value, fresh.AsOf, M365States.Ok);
        }
        try
        {
            var value = await fetch();
            var entry = new Entry<T>(value, clock.UtcNow);
            cache.Set(key + ":fresh", entry, freshness);
            cache.Set(key + ":last", entry, LastKnown);
            return (value, entry.AsOf, M365States.Ok);
        }
        catch (GraphCallException ex)
        {
            var state = ex.Kind switch
            {
                "unauthorized" => M365States.Reconnect,
                "no_mailbox" => M365States.NoMailbox,
                "forbidden" => M365States.AdminSetup,
                _ => M365States.Degraded,
            };
            var last = state == M365States.Degraded ? cache.Get<Entry<T>>(key + ":last") : null;
            return (last?.Value, last?.AsOf, state);
        }
    }

    private bool AllowRefresh()
    {
        // A window that starts at the first forced refresh and lasts one minute.
        var key = $"graph-refresh:{context.TenantId:N}:{context.ActorId:N}";
        var count = cache.GetOrCreate(key, e =>
        {
            e.AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(1);
            return new int[1];
        })!;
        return Interlocked.Increment(ref count[0]) <= RefreshesPerMinute;
    }

    private string Key(string feature, string query) =>
        $"graph:{context.RequireTenant():N}:{context.ActorId:N}:{context.SessionId:N}:{feature}:{query}";
}

/// <summary>Microsoft 365 adapter for <see cref="IMailbox"/> (spec 018 US2). Graph types stay here.</summary>
public sealed class GraphMailbox(DelegatedAccessService access, IMicrosoftGraph graph, GraphReadCache cache) : IMailbox
{
    public const string ComposeLink = "https://outlook.office.com/mail/deeplink/compose";

    private sealed record Inbox(int Unread, IReadOnlyList<MailMessage> Messages);

    public async Task<MailSummary> GetInboxAsync(MailQuery query, CancellationToken ct = default)
    {
        var grant = await access.AcquireAsync(Microsoft365Features.Mail, ct);
        if (grant.State != M365States.Ok)
        {
            return new MailSummary(grant.State, null, null, [], null);
        }
        var top = Math.Clamp(query.Top, 1, grant.Options.MaxMessages);
        var focused = query.View == "focused";
        var preview = grant.Options.PreviewLine && grant.Scopes.Contains("Mail.Read");
        var (value, asOf, state) = await cache.ReadAsync(Microsoft365Features.Mail, $"{(focused ? "focused" : "all")}:{top}:{preview}",
            GraphReadCache.MailFreshness, query.Refresh, async () =>
            {
                var folder = await graph.DelegatedSendAsync(grant.AccessToken!, HttpMethod.Get, "me/mailFolders/inbox?$select=unreadItemCount", null, ct);
                var select = "id,subject,from,receivedDateTime,isRead,importance,hasAttachments,webLink" + (preview ? ",bodyPreview" : string.Empty);
                var path = $"me/mailFolders/inbox/messages?$top={top}&$select={select}"
                    + (focused ? "&$filter=inferenceClassification%20eq%20'focused'" : "&$orderby=receivedDateTime%20desc");
                var list = await graph.DelegatedSendAsync(grant.AccessToken!, HttpMethod.Get, path, null, ct);
                var messages = list.GetProperty("value").EnumerateArray().Select(m => Map(m, preview))
                    .OrderByDescending(m => m.ReceivedAt).Take(top).ToList();
                return new Inbox(folder.TryGetProperty("unreadItemCount", out var u) && u.TryGetInt32(out var n) ? n : 0, messages);
            });
        return value is null
            ? new MailSummary(state, null, null, [], null)
            : new MailSummary(state, asOf, value.Unread, value.Messages, ComposeLink);
    }

    private static MailMessage Map(JsonElement m, bool preview)
    {
        var from = m.TryGetProperty("from", out var f) && f.ValueKind == JsonValueKind.Object && f.TryGetProperty("emailAddress", out var e) ? e : default;
        return new MailMessage(
            m.GetProperty("id").GetString()!,
            Text(from, "name") ?? Text(from, "address") ?? string.Empty,
            Text(from, "address"),
            Text(m, "subject") ?? string.Empty,
            m.GetProperty("receivedDateTime").GetDateTimeOffset(),
            m.TryGetProperty("isRead", out var r) && r.ValueKind == JsonValueKind.True,
            (Text(m, "importance") ?? "normal").ToLowerInvariant(),
            m.TryGetProperty("hasAttachments", out var a) && a.ValueKind == JsonValueKind.True,
            preview ? PlainText(Text(m, "bodyPreview")) : null,
            Text(m, "webLink") ?? string.Empty);
    }

    /// <summary>
    /// The preview line as plain text only (FR-008): control characters removed, whitespace collapsed, at most 200
    /// characters. The web layer renders it as text, never as HTML.
    /// </summary>
    public static string? PlainText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }
        var sb = new StringBuilder(value.Length);
        var space = false;
        foreach (var ch in value)
        {
            if (char.IsWhiteSpace(ch) || char.IsControl(ch))
            {
                space = sb.Length > 0;
                continue;
            }
            if (space)
            {
                sb.Append(' ');
                space = false;
            }
            sb.Append(ch);
        }
        return sb.Length <= 200 ? sb.ToString() : sb.ToString(0, 200);
    }

    internal static string? Text(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}

/// <summary>Microsoft 365 adapter for <see cref="ICalendar"/> (spec 018 US3): the calendar view, so series come expanded.</summary>
public sealed class GraphCalendar(DelegatedAccessService access, IMicrosoftGraph graph, GraphReadCache cache) : ICalendar
{
    private sealed record Occurrences(IReadOnlyList<CalendarOccurrence> Items);

    public async Task<CalendarResult> GetEventsAsync(CalendarQuery query, CancellationToken ct = default)
    {
        var grant = await access.AcquireAsync(Microsoft365Features.Calendar, ct);
        if (grant.State != M365States.Ok)
        {
            return new CalendarResult(grant.State, null, []);
        }
        var to = query.To > query.From.AddDays(grant.Options.MaxDays) ? query.From.AddDays(grant.Options.MaxDays) : query.To;
        var from = query.From.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
        var until = to.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
        var (value, asOf, state) = await cache.ReadAsync(Microsoft365Features.Calendar, $"{from}:{until}", GraphReadCache.CalendarFreshness, query.Refresh,
            async () =>
            {
                var path = $"me/calendarView?startDateTime={from}&endDateTime={until}&$top=250&$orderby=start/dateTime"
                    + "&$select=id,subject,start,end,isAllDay,showAs,sensitivity,isCancelled,responseStatus,location,onlineMeeting,webLink";
                var list = await graph.DelegatedSendAsync(grant.AccessToken!, HttpMethod.Get, path, null, ct);
                return new Occurrences(list.GetProperty("value").EnumerateArray().Select(Map).ToList());
            });
        return new CalendarResult(state, asOf, value?.Items ?? []);
    }

    private static CalendarOccurrence Map(JsonElement e)
    {
        var location = e.TryGetProperty("location", out var l) ? GraphMailbox.Text(l, "displayName") : null;
        var join = e.TryGetProperty("onlineMeeting", out var o) ? GraphMailbox.Text(o, "joinUrl") : null;
        var response = e.TryGetProperty("responseStatus", out var rs) ? GraphMailbox.Text(rs, "response") : null;
        return new CalendarOccurrence(
            e.GetProperty("id").GetString()!,
            GraphMailbox.Text(e, "subject"),
            GraphTime.Read(e.GetProperty("start")),
            GraphTime.Read(e.GetProperty("end")),
            e.TryGetProperty("isAllDay", out var a) && a.ValueKind == JsonValueKind.True,
            GraphTime.ShowAs(GraphMailbox.Text(e, "showAs")),
            GraphMailbox.Text(e, "sensitivity") is "private" or "confidential",
            e.TryGetProperty("isCancelled", out var c) && c.ValueKind == JsonValueKind.True,
            response switch
            {
                "organizer" => "organizer",
                "accepted" => "accepted",
                "tentativelyAccepted" => "tentative",
                "declined" => "declined",
                "notResponded" => "not_responded",
                _ => "none",
            },
            string.IsNullOrWhiteSpace(location) ? null : location,
            string.IsNullOrWhiteSpace(join) ? null : join,
            GraphMailbox.Text(e, "webLink") ?? string.Empty);
    }
}

/// <summary>Microsoft 365 adapter for <see cref="IAvailability"/> (spec 018 US4): <c>getSchedule</c> as the viewer, status only.</summary>
public sealed class GraphAvailability(DelegatedAccessService access, IMicrosoftGraph graph, GraphReadCache cache) : IAvailability
{
    private sealed record Schedule(bool Shared, IReadOnlyList<AvailabilityBlock> Blocks);

    public async Task<AvailabilityResult> GetAsync(string colleagueAddress, DateTimeOffset from, DateTimeOffset to, CancellationToken ct = default)
    {
        var grant = await access.AcquireAsync(Microsoft365Features.FreeBusy, ct);
        if (grant.State != M365States.Ok)
        {
            return new AvailabilityResult(grant.State, false, []);
        }
        var start = from.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture);
        var end = to.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture);
        var (value, _, state) = await cache.ReadAsync(Microsoft365Features.FreeBusy, $"{colleagueAddress.ToLowerInvariant()}:{start}:{end}",
            GraphReadCache.CalendarFreshness, false, async () =>
            {
                var body = new
                {
                    schedules = new[] { colleagueAddress },
                    startTime = new { dateTime = start, timeZone = "UTC" },
                    endTime = new { dateTime = end, timeZone = "UTC" },
                    availabilityViewInterval = 30,
                };
                var result = await graph.DelegatedSendAsync(grant.AccessToken!, HttpMethod.Post, "me/calendar/getSchedule", body, ct);
                var schedule = result.GetProperty("value").EnumerateArray().FirstOrDefault();
                if (schedule.ValueKind != JsonValueKind.Object || schedule.TryGetProperty("error", out _)
                    || !schedule.TryGetProperty("scheduleItems", out var items))
                {
                    return new Schedule(false, []);
                }
                return new Schedule(true, Merge(items.EnumerateArray()
                    .Select(i => new AvailabilityBlock(GraphTime.ShowAs(GraphMailbox.Text(i, "status")), GraphTime.Read(i.GetProperty("start")), GraphTime.Read(i.GetProperty("end"))))
                    .Where(b => b.Status is not ("free" or "unknown"))
                    .Select(b => b with { Start = b.Start < from ? from : b.Start, End = b.End > to ? to : b.End })
                    .Where(b => b.End > b.Start)
                    .OrderBy(b => b.Start)));
            });
        return value is null ? new AvailabilityResult(state, false, []) : new AvailabilityResult(state, value.Shared, value.Blocks);
    }

    /// <summary>Joins overlapping or touching blocks of the same status, so the text reads "busy until 11:00".</summary>
    private static List<AvailabilityBlock> Merge(IEnumerable<AvailabilityBlock> blocks)
    {
        var merged = new List<AvailabilityBlock>();
        foreach (var b in blocks)
        {
            if (merged.Count > 0 && merged[^1].Status == b.Status && b.Start <= merged[^1].End)
            {
                merged[^1] = merged[^1] with { End = b.End > merged[^1].End ? b.End : merged[^1].End };
            }
            else
            {
                merged.Add(b);
            }
        }
        return merged;
    }
}

internal static class GraphTime
{
    /// <summary>Graph dateTimeTimeZone in UTC (the adapter asks for <c>outlook.timezone="UTC"</c>).</summary>
    public static DateTimeOffset Read(JsonElement value)
    {
        var text = value.GetProperty("dateTime").GetString()!;
        var parsed = DateTime.Parse(text, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);
        return new DateTimeOffset(DateTime.SpecifyKind(parsed, DateTimeKind.Utc));
    }

    public static string ShowAs(string? value) => value switch
    {
        "free" => "free",
        "tentative" => "tentative",
        "busy" => "busy",
        "oof" => "oof",
        "workingElsewhere" => "working_elsewhere",
        _ => "unknown",
    };
}
