using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TeamBeats.Modules.Notifications.Contracts;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Primitives;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.Calendar;

/// <summary>
/// The calendar clock (spec 037 US9, FR-032), run every minute per tenant. Each person registered for an occurrence or
/// who answered Going gets one reminder at the event's offset, never twice and never for a place taken after the
/// reminder time; when an event is cancelled, everyone registered, waiting or Going for an occurrence still ahead is
/// told once. A row in <c>calendar.reminder_sent</c> is written before each notice, so a retry never repeats one.
/// </summary>
public sealed class CalendarMaintenance(IDbSession session, ITenantSettingsReader tenantSettings, INotificationSender notifications)
{
    public const string ReminderTemplate = "calendar.event_reminder";
    public const string CancelledTemplate = "calendar.event_cancelled";

    public sealed record Run(int Reminded, int ToldOfCancellation);

    public async Task<Run> RunDueAsync(DateTimeOffset now, CancellationToken ct)
    {
        var tag = (await tenantSettings.GetLocaleSettingsAsync(ct)).DefaultLocale;
        var reminded = 0;
        var told = 0;

        // Reminders: occurrences starting within the longest offset (1 day) whose own reminder time has come.
        var due = await session.QueryAsync(
            $"""
            select {EventRow.Columns} from calendar.event
            where status = 'published' and reminder <> 'none' and (rsvp or registration) and series_end > @now
              and coalesce(start_at, start_date::timestamptz) < @horizon
            limit 500
            """, new { now, horizon = now.AddDays(3) }, EventRow.Read, ct);
        foreach (var row in due)
        {
            var offset = EventReminders.Offset(row.Reminder)!.Value;
            foreach (var o in CompanyEvents.Expand(row, now, now + offset))
            {
                var start = CompanyEvents.OccurrenceStart(o, row);
                if (start <= now || start - offset > now)
                {
                    continue;
                }
                var people = await RecipientsAsync(row, o.Key, "reminder", start - offset, includeWaiting: false, ct);
                if (people.Count == 0)
                {
                    continue;
                }
                await notifications.SendAsync(new NotificationRequest(people, NotificationCategories.Events, ReminderTemplate, Link(row, o),
                    new Dictionary<string, string>
                    {
                        ["event"] = CompanyEvents.PickFor(row.Titles, tag),
                        ["when"] = CompanyEvents.WhenText(row, o, tag),
                        ["place"] = row.Location ?? row.OnlineUrl ?? string.Empty,
                    }, DedupeKey: $"calendar:event:{row.Id:N}:{o.Key}:reminder"), ct);
                reminded += people.Count;
            }
        }

        // Cancellation notices for occurrences that had not started when the event was cancelled.
        var cancelled = await session.QueryAsync(
            $"""
            select {EventRow.Columns} from calendar.event
            where status = 'cancelled' and cancelled_at > @since and (rsvp or registration) and series_end > @now
            limit 500
            """, new { since = now - CompanyEvents.CancelledShownFor, now }, EventRow.Read, ct);
        foreach (var row in cancelled)
        {
            foreach (var o in CompanyEvents.Expand(row, row.CancelledAt!.Value, row.CancelledAt.Value.AddYears(Recurrence.MaxYears)))
            {
                if (CompanyEvents.OccurrenceStart(o, row) <= row.CancelledAt.Value)
                {
                    continue;
                }
                var people = await RecipientsAsync(row, o.Key, "cancelled", null, includeWaiting: true, ct);
                if (people.Count == 0)
                {
                    continue;
                }
                await notifications.SendAsync(new NotificationRequest(people, NotificationCategories.Events, CancelledTemplate, Link(row, o),
                    new Dictionary<string, string> { ["event"] = CompanyEvents.PickFor(row.Titles, tag), ["when"] = CompanyEvents.WhenText(row, o, tag) },
                    DedupeKey: $"calendar:event:{row.Id:N}:{o.Key}:cancelled"), ct);
                told += people.Count;
            }
        }
        return new Run(reminded, told);
    }

    /// <summary>
    /// The people to tell for one occurrence who were not told yet, recorded as told in the same step. With a cut-off,
    /// only places taken or answers given before it count.
    /// </summary>
    private async Task<IReadOnlyList<Guid>> RecipientsAsync(EventRow row, string key, string kind, DateTimeOffset? before, bool includeWaiting, CancellationToken ct) =>
        await session.QueryAsync(
            """
            insert into calendar.reminder_sent (tenant_id, event_id, occurrence_key, user_id, kind, sent_at)
            select platform.current_tenant_id(), @id, @key, p.user_id, @kind, now()
            from (
              select user_id from calendar.registration
              where event_id = @id and occurrence_key = @key and (state = 'registered' or @waiting) and (@before::timestamptz is null or joined_at <= @before)
              union
              select user_id from calendar.response
              where event_id = @id and occurrence_key = @key and answer = 'going' and (@before::timestamptz is null or answered_at <= @before)
            ) p
            on conflict do nothing
            returning user_id
            """, new { id = row.Id, key, kind, before, waiting = includeWaiting }, r => r.GetGuid(0), ct);

    private static string Link(EventRow row, Occurrence o)
    {
        var date = o.StartDate ?? DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(o.Start!.Value, row.Zone).DateTime);
        return $"/calendar?view=agenda&date={date:yyyy-MM-dd}";
    }
}

/// <summary>Runs <see cref="CalendarMaintenance"/> every minute for each active tenant.</summary>
public sealed class CalendarMaintenanceWorker(IServiceScopeFactory scopes, IClock clock, ILogger<CalendarMaintenanceWorker> logger) : BackgroundService
{
    public const string InProcessKey = "Calendar:Maintenance:InProcess";
    public static readonly TimeSpan Tick = TimeSpan.FromMinutes(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            await RunOnceAsync(stoppingToken);
            await Task.Delay(Tick, stoppingToken);
        }
    }

    private async Task RunOnceAsync(CancellationToken ct)
    {
        IReadOnlyList<TenantInfo> tenants;
        try
        {
            await using var listScope = scopes.CreateAsyncScope();
            tenants = await listScope.ServiceProvider.GetRequiredService<ITenantLister>().ListAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Calendar maintenance could not list tenants");
            return;
        }
        var now = clock.UtcNow;
        foreach (var tenant in tenants.Where(t => t.Status == TenantLifecycleStatus.Active))
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var context = scope.ServiceProvider.GetRequiredService<TenantContextAccessor>();
                context.SetTenant(tenant.Id);
                context.SetActor(ActorType.System, null);
                await scope.ServiceProvider.GetRequiredService<CalendarMaintenance>().RunDueAsync(now, ct);
                await scope.ServiceProvider.GetRequiredService<IDbSession>().CommitAsync(ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Calendar maintenance failed for tenant {TenantId}", tenant.Id);
            }
        }
    }
}
