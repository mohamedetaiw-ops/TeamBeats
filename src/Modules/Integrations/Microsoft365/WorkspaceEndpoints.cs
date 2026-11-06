using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using TeamBeats.Entitlements.Contracts;
using TeamBeats.Modules.Identity.Contracts;
using TeamBeats.Modules.Integrations.Contracts;
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
    string WebLink);

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
            ITenantSettingsReader settings, IClock clock, CancellationToken ct) =>
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
            return Results.Ok(new CalendarResponse(result.State, result.AsOf, zone.Id, from, to, Shape(result.Occurrences, zone, hidePrivate == true)));
        });

        app.MapGet("/api/v1/people/{userId:guid}/availability", async (Guid userId, IEntitlementService entitlements, IUserDirectory users,
            ITenantMemberships memberships, IAvailability availability, ITenantSettingsReader settings, IClock clock, CancellationToken ct) =>
        {
            await entitlements.DemandAsync(Capabilities.IntegrationMicrosoft365, ct);
            // Until the people profile module (spec 010) decides who may see a profile, a viewer may see active members of
            // the same tenant. Users of another tenant are not found (row-level security).
            var colleague = await users.GetAsync(userId, ct);
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
        occurrences
            .Where(o => !o.IsCancelled && o.Response != "declined")
            .OrderBy(o => o.IsAllDay ? DateOnly.FromDateTime(o.Start.UtcDateTime) : DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(o.Start, zone).DateTime))
            .ThenBy(o => o.IsAllDay ? 0 : 1)
            .ThenBy(o => o.Start)
            .Select(o => new CalendarItemView(o.Id, "outlook", hidePrivate && o.IsPrivate ? null : o.Subject, o.Start, o.End, o.IsAllDay, o.IsPrivate,
                o.ShowAs == "free", o.ShowAs == "tentative" || o.Response == "tentative", o.Location, o.OnlineJoinUrl, o.WebLink))
            .ToList();

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
