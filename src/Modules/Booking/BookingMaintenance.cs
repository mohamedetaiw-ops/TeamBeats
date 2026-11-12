using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TeamBeats.Messaging;
using TeamBeats.Modules.Booking.Contracts;
using TeamBeats.Modules.Notifications.Contracts;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Primitives;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.Booking;

/// <summary>
/// The booking clock (spec 031 US3 and US4, FR-007, FR-009, FR-010), run every minute per tenant: tentative holds
/// that ran out expire, holders are told when check-in opens, bookings nobody checked in to by the end of the grace
/// period are released so others can book them, and people who reach the tenant's no-show limit are reminded of the
/// rule and, when the tenant says so, may book fewer days ahead for a while. Resources with check-in off are never
/// released. Bookings that ended more than a year ago lose who held them (section 8).
/// </summary>
public sealed class BookingMaintenance(
    IDbSession session,
    BookingRules rules,
    BookingApprovals approvals,
    ITenantSettingsReader tenantSettings,
    INotificationSender notifications,
    IOutbox outbox,
    BookingActivity activity)
{
    public const string CheckInOpenTemplate = "booking.check_in_open";
    public const string ReleasedTemplate = "booking.released";
    public const string NoShowRuleTemplate = "booking.no_show_rule";
    public const string NoShowRestrictedTemplate = "booking.no_show_restricted";
    public const int Batch = 500;

    public const int RetentionDays = 365;

    public sealed record Run(int Expired, int Reminded, int Released, int Noticed, int Anonymized = 0);

    public async Task<Run> RunDueAsync(DateTimeOffset now, CancellationToken ct)
    {
        var expired = await approvals.ExpireDueAsync(now, ct);
        var settings = await rules.SettingsAsync(ct);
        var locale = (await tenantSettings.GetLocaleSettingsAsync(ct)).DefaultLocale;

        // Check-in opens: one reminder per booking (FR-013).
        var open = await session.QueryAsync(
            """
            with due as (
              update booking.booking b set reminded_at = @now
              from booking.resource r
              where r.id = b.resource_id and r.check_in and b.status = 'confirmed' and b.reminded_at is null
                and b.starts_at - make_interval(mins => @before) <= @now and greatest(b.starts_at, b.created_at) + make_interval(mins => @grace) > @now
                and b.id in (select x.id from booking.booking x join booking.resource y on y.id = x.resource_id
                             where y.check_in and x.status = 'confirmed' and x.reminded_at is null and x.starts_at <= @horizon
                               and greatest(x.starts_at, x.created_at) + make_interval(mins => @grace) > @now
                             order by x.starts_at limit @batch)
              returning b.id, b.holder_id, b.starts_at, b.ends_at, r.names::text, r.site_id
            )
            select d.id, d.holder_id, d.starts_at, d.ends_at, d.names, s.time_zone from due d join booking.site s on s.id = d.site_id
            """, new { now, before = settings.CheckInBeforeMinutes, grace = settings.CheckInGraceMinutes, horizon = now.AddMinutes(settings.CheckInBeforeMinutes), batch = Batch },
            r => (Id: r.GetGuid(0), Holder: r.GetGuid(1), Start: r.GetFieldValue<DateTimeOffset>(2), End: r.GetFieldValue<DateTimeOffset>(3), Names: Names.Read(r, 4),
                Zone: r.GetString(5)), ct);
        foreach (var b in open)
        {
            var parameters = BookingCancellations.Describe(b.Names, b.Zone, b.Start, b.End, locale);
            parameters["grace"] = settings.CheckInGraceMinutes.ToString(CultureInfo.InvariantCulture);
            await notifications.SendAsync(new NotificationRequest([b.Holder], NotificationCategories.Bookings, CheckInOpenTemplate, Bookings.Link(b.Id), parameters,
                DedupeKey: $"booking:{b.Id:N}:check_in_open"), ct);
        }

        // No check-in by the end of the grace period: released (US4 scenario 2).
        var released = await session.QueryAsync(
            """
            with gone as (
              update booking.booking b set status = 'released', ended_at = @now, end_reason = 'no_show', row_version = b.row_version + 1
              from booking.resource r
              where r.id = b.resource_id and r.check_in and b.status = 'confirmed'
                and greatest(b.starts_at, b.created_at) + make_interval(mins => @grace) <= @now
                and b.id in (select x.id from booking.booking x join booking.resource y on y.id = x.resource_id
                             where y.check_in and x.status = 'confirmed' and x.starts_at <= @now order by x.starts_at limit @batch)
              returning b.id, b.resource_id, b.holder_id, b.starts_at, b.ends_at, r.kind, r.site_id, r.zone_id, r.names::text
            )
            select g.id, g.resource_id, g.holder_id, g.starts_at, g.ends_at, g.kind, g.site_id, g.zone_id, g.names, s.time_zone
            from gone g join booking.site s on s.id = g.site_id
            order by g.starts_at
            """, new { now, grace = settings.CheckInGraceMinutes, batch = Batch },
            r => (Id: r.GetGuid(0), Resource: r.GetGuid(1), Holder: r.GetGuid(2), Start: r.GetFieldValue<DateTimeOffset>(3), End: r.GetFieldValue<DateTimeOffset>(4),
                Kind: r.GetString(5), Site: r.GetGuid(6), ZoneId: r.Get<Guid?>("zone_id"), Names: Names.Read(r, 8), Zone: r.GetString(9)), ct);
        foreach (var b in released)
        {
            await outbox.EnqueueAsync(new BookingReleased(b.Id, b.Resource, b.Site, b.Kind, b.Holder), ct);
            await activity.RecordAsync("released", b.Kind, b.Site, ct);
            await notifications.SendAsync(new NotificationRequest([b.Holder], NotificationCategories.Bookings, ReleasedTemplate,
                $"/booking?site={b.Site}&kind={b.Kind}" + (b.ZoneId is { } z ? $"&zone={z}" : ""),
                BookingCancellations.Describe(b.Names, b.Zone, b.Start, b.End, locale), DedupeKey: $"booking:{b.Id:N}:released"), ct);
        }

        // The no-show rule (US4 scenario 3, FR-010): reminded once per window on reaching the limit.
        var noticed = 0;
        foreach (var holder in released.Select(b => b.Holder).Distinct())
        {
            var since = now.AddDays(-settings.NoShowWindowDays);
            var count = await session.ScalarAsync<int>(
                "select count(*)::int from booking.booking where holder_id = @holder and end_reason = 'no_show' and ended_at > @since", new { holder, since }, ct);
            if (count < settings.NoShowLimit || await session.ScalarAsync<bool>(
                    "select exists (select 1 from booking.no_show_notice where user_id = @holder and noticed_at > @since)", new { holder, since }, ct))
            {
                continue;
            }
            DateTimeOffset? until = settings.NoShowRestrict ? now.AddDays(settings.NoShowRestrictDays) : null;
            int? advance = settings.NoShowRestrict ? settings.NoShowAdvanceDays : null;
            await session.ExecAsync(
                """
                insert into booking.no_show_notice (tenant_id, user_id, noticed_at, no_shows, restricted_until, advance_days)
                values (platform.current_tenant_id(), @holder, @now, @count, @until, @advance)
                on conflict (tenant_id, user_id) do update set noticed_at = excluded.noticed_at, no_shows = excluded.no_shows, restricted_until = excluded.restricted_until,
                  advance_days = excluded.advance_days
                """, new { holder, now, count, until, advance }, ct);
            var parameters = new Dictionary<string, string>
            {
                ["count"] = count.ToString(CultureInfo.InvariantCulture),
                ["days"] = settings.NoShowWindowDays.ToString(CultureInfo.InvariantCulture),
                ["restrictDays"] = settings.NoShowRestrictDays.ToString(CultureInfo.InvariantCulture),
                ["advanceDays"] = settings.NoShowAdvanceDays.ToString(CultureInfo.InvariantCulture),
            };
            await notifications.SendAsync(new NotificationRequest([holder], NotificationCategories.Bookings, settings.NoShowRestrict ? NoShowRestrictedTemplate : NoShowRuleTemplate,
                "/booking/bookings", parameters, DedupeKey: $"booking:no_show:{holder:N}:{now:yyyyMMddHHmm}"), ct);
            noticed++;
        }
        // Section 8: a booking that ended more than a year ago keeps only what counts need.
        var cutoff = now.AddDays(-RetentionDays);
        var anonymized = await session.ExecAsync(
            """
            update booking.booking set holder_id = '00000000-0000-0000-0000-000000000000', booked_by = '00000000-0000-0000-0000-000000000000', decided_by = null,
              title = null, decision_note = null, row_version = row_version + 1
            where id in (select id from booking.booking where ends_at < @cutoff and holder_id <> '00000000-0000-0000-0000-000000000000' order by ends_at limit @batch)
            """, new { cutoff, batch = Batch }, ct);
        await session.ExecAsync(
            """
            update booking.series s set holder_id = '00000000-0000-0000-0000-000000000000', created_by = '00000000-0000-0000-0000-000000000000'
            where s.holder_id <> '00000000-0000-0000-0000-000000000000' and s.until < @day
              and not exists (select 1 from booking.booking b where b.series_id = s.id and b.holder_id <> '00000000-0000-0000-0000-000000000000')
            """, new { day = DateOnly.FromDateTime(cutoff.UtcDateTime) }, ct);
        await session.ExecAsync("delete from booking.no_show_notice where noticed_at < @cutoff and (restricted_until is null or restricted_until < @now)", new { cutoff, now }, ct);
        return new Run(expired, open.Count, released.Count, noticed, anonymized);
    }
}

/// <summary>
/// Runs <see cref="BookingMaintenance"/> every minute for every active tenant, each in its own scope and transaction,
/// as the system actor. Hosted by the worker, and by the development host when <c>Booking:Maintenance:InProcess</c> is true.
/// </summary>
public sealed class BookingMaintenanceWorker(IServiceScopeFactory scopes, IClock clock, ILogger<BookingMaintenanceWorker> logger) : BackgroundService
{
    public const string InProcessKey = "Booking:Maintenance:InProcess";
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
            logger.LogWarning(ex, "Booking maintenance could not list tenants");
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
                await scope.ServiceProvider.GetRequiredService<BookingMaintenance>().RunDueAsync(now, ct);
                await scope.ServiceProvider.GetRequiredService<IDbSession>().CommitAsync(ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Booking maintenance failed for tenant {TenantId}", tenant.Id);
            }
        }
    }
}
