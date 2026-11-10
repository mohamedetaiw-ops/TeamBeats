using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using TeamBeats.Authorization.Contracts;
using TeamBeats.Entitlements.Contracts;
using TeamBeats.Modules.Hr.Contracts;
using TeamBeats.Modules.Identity.Contracts;
using TeamBeats.Modules.Integrations.Contracts;
using TeamBeats.Modules.People.Contracts;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Primitives;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.Integrations.Microsoft365;

/// <summary>An occurrence as My calendar shows it, with the text markers of FR-014.</summary>
public sealed record CalendarItemView(
    string Id,
    string Source,
    string? Subject,
    DateTimeOffset Start,
    DateTimeOffset End,
    bool IsAllDay,
    bool IsPrivate,
    bool Free,
    bool Tentative,
    string? Location,
    string? OnlineJoinUrl,
    string WebLink,
    bool HalfDay = false);

public sealed record CalendarResponse(string State, DateTimeOffset? AsOf, string TimeZone, DateTimeOffset From, DateTimeOffset To, IReadOnlyList<CalendarItemView> Items);

public sealed record AvailabilityResponse(string State, bool Shared, IReadOnlyList<AvailabilityBlock> Blocks);

/// <summary>
/// My mail, My calendar and a colleague's free and busy (spec 018 US2 to US4). Every endpoint needs
/// <c>integration.microsoft365</c>; mailbox access is Microsoft 365's decision for the signed-in employee.
/// </summary>
public static class WorkspaceEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        var me = app.MapGroup("/api/v1/me").RequireTenantUser().WithTags("Integrations: mail and calendar");

        me.MapGet("/mail", async (string? view, int? top, bool? refresh, IEntitlementService entitlements, IMailbox mailbox, CancellationToken ct) =>
        {
            await entitlements.DemandAsync(Capabilities.IntegrationMicrosoft365, ct);
            var errors = new ValidationErrors();
            if (view is not (null or "focused" or "all"))
            {
                errors.Add("view", "validation.mail.view_invalid");
            }
            if (top is < 1 or > 10)
            {
                errors.Add("top", "validation.mail.top_range");
            }
            errors.ThrowIfAny();
            return Results.Ok(await mailbox.GetInboxAsync(new MailQuery(view ?? "focused", top ?? 5, refresh == true), ct));
        });

        me.MapGet("/calendar", async (int? days, bool? hidePrivate, bool? refresh, IEntitlementService entitlements, ICalendar calendar,
            ITenantSettingsReader settings, IClock clock, IWorkingCalendar working, ITenantContext context, CancellationToken ct) =>
        {
            await entitlements.DemandAsync(Capabilities.IntegrationMicrosoft365, ct);
            if (days is < 1 or > 14)
            {
                throw AppException.Validation("days", "validation.calendar.days_range");
            }
            var zone = await ZoneAsync(settings, ct);
            var from = StartOfDay(clock.UtcNow, zone);
            var to = from.AddDays(days ?? 7);
            var result = await calendar.GetEventsAsync(new CalendarQuery(from, to, refresh == true), ct);
            var items = Shape(result.Occurrences, zone, hidePrivate == true);
            if (result.State is M365States.Ok or M365States.Degraded)
            {
                // FR-017: TeamBeats items join the Outlook ones, labelled by source. R1 has location holidays (spec 022);
                // registered events (009) and approved leave (022) come with those R2 features.
                var snapshot = await working.ForUserAsync(context.RequireUser(), ct);
                items = Order(items.Concat(Holidays(snapshot, DateOnly.FromDateTime(from.DateTime), days ?? 7)), zone);
            }
            return Results.Ok(new CalendarResponse(result.State, result.AsOf, zone.Id, from, to, items));
        });

        app.MapGet("/api/v1/people/{userId:guid}/availability", async (Guid userId, IEntitlementService entitlements, IUserDirectory users,
            ITenantMemberships memberships, IAvailability availability, ITenantSettingsReader settings, IClock clock, IAuthorizationService authz,
            IProfiles profiles, ITenantContext context, CancellationToken ct) =>
        {
            await entitlements.DemandAsync(Capabilities.IntegrationMicrosoft365, ct);
            // Who may see a colleague's free and busy is the profile visibility rule (spec 010 FR-014): the viewer needs
            // `people.view` and the colleague an active profile the viewer may open. Users of another tenant are not found.
            if (!await authz.HoldsAnywhereAsync(Permissions.PeopleView, ct))
            {
                throw AppException.NotFound();
            }
            var visible = await profiles.ViewAsync(context.RequireUser(), [userId], ct);
            var colleague = visible is [{ Active: true }] ? await users.GetAsync(userId, ct) : null;
            var membership = colleague is null ? null : await memberships.GetAsync(userId, ct);
            if (colleague is null || membership is not { Status: MembershipStatus.Active } || string.IsNullOrEmpty(colleague.Email))
            {
                throw AppException.NotFound();
            }
            var zone = await ZoneAsync(settings, ct);
            var now = clock.UtcNow;
            var result = await availability.GetAsync(colleague.Email, now, StartOfDay(now, zone).AddDays(1), ct);
            return Results.Ok(new AvailabilityResponse(result.State, result.Shared, result.Blocks));
        }).RequireTenantUser().WithTags("Integrations: mail and calendar");
    }

    /// <summary>
    /// FR-014 and FR-016: cancelled and declined hidden, free and tentative marked in text, private subjects hidden when the
    /// placement asks, and per day all-day items first.
    /// </summary>
    public static IReadOnlyList<CalendarItemView> Shape(IEnumerable<CalendarOccurrence> occurrences, TimeZoneInfo zone, bool hidePrivate) =>
        Order(occurrences
            .Where(o => !o.IsCancelled && o.Response != "declined")
            .Select(o => new CalendarItemView(o.Id, "outlook", hidePrivate && o.IsPrivate ? null : o.Subject, o.Start, o.End, o.IsAllDay, o.IsPrivate,
                o.ShowAs == "free", o.ShowAs == "tentative" || o.Response == "tentative", o.Location, o.OnlineJoinUrl, o.WebLink)), zone);

    /// <summary>Per day, all-day items first, then by start; all-day items keep the date they carry.</summary>
    private static IReadOnlyList<CalendarItemView> Order(IEnumerable<CalendarItemView> items, TimeZoneInfo zone) =>
        items
            .OrderBy(o => o.IsAllDay ? DateOnly.FromDateTime(o.Start.UtcDateTime) : DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(o.Start, zone).DateTime))
            .ThenBy(o => o.IsAllDay ? 0 : 1)
            .ThenBy(o => o.Start)
            .ToList();

    /// <summary>
    /// Holidays of the person's working calendar (spec 022) in the shown days, as all-day items with source <c>holiday</c>
    /// and no subject or link: the page names them. Weekend days are not listed.
    /// </summary>
    public static IEnumerable<CalendarItemView> Holidays(WorkingCalendarSnapshot calendar, DateOnly first, int days)
    {
        for (var date = first; date < first.AddDays(days); date = date.AddDays(1))
        {
            if (!calendar.WeekDays.Contains(date.DayOfWeek) || calendar.WorkingFraction(date) >= 1m)
            {
                continue;
            }
            var start = new DateTimeOffset(date.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
            yield return new CalendarItemView($"holiday:{date:yyyy-MM-dd}", "holiday", null, start, start.AddDays(1), true, false, false, false, null, null,
                string.Empty, calendar.WorkingFraction(date) > 0m);
        }
    }

    private static async Task<TimeZoneInfo> ZoneAsync(ITenantSettingsReader settings, CancellationToken ct)
    {
        var id = (await settings.GetLocaleSettingsAsync(ct)).TimeZone;
        return TimeZoneInfo.TryFindSystemTimeZoneById(id, out var zone) ? zone : TimeZoneInfo.Utc;
    }

    private static DateTimeOffset StartOfDay(DateTimeOffset instant, TimeZoneInfo zone)
    {
        var local = TimeZoneInfo.ConvertTime(instant, zone);
        var midnight = local.Date;
        return new DateTimeOffset(midnight, zone.GetUtcOffset(midnight));
    }
}
