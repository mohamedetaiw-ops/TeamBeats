using System.Text.Json;
using TeamBeats.Audit.Contracts;
using TeamBeats.Entitlements.Contracts;
using TeamBeats.Modules.Notifications.Contracts;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Primitives;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.Experience.Pages;

/// <param name="GoLiveAt">Tenant-local date and time, without an offset.</param>
/// <param name="ExpiresAt">Tenant-local date and time after which the page returns to the release in force before; null for none.</param>
public sealed record SchedulePageRequest(DateTime? GoLiveAt, DateTime? ExpiresAt, string? Reason);

/// <param name="TimeZone">The tenant's time zone, in which the times were set and are shown.</param>
/// <param name="Findings">For a schedule that failed at go-live, the codes of the blocking findings.</param>
public sealed record PageScheduleView(Guid Id, string Status, DateTimeOffset GoLiveAt, DateTimeOffset? ExpiresAt, string TimeZone, string? Reason,
    Guid ScheduledBy, DateTimeOffset ScheduledAt, int? Release, IReadOnlyList<string> Findings);

/// <param name="TimeZone">The tenant's time zone, in which schedules are set.</param>
/// <param name="Current">The waiting schedule, else one waiting to expire, else the last one; null for none.</param>
public sealed record PageScheduleState(string TimeZone, PageScheduleView? Current);

public static class PageScheduleStatuses
{
    public const string Scheduled = "scheduled";
    public const string Live = "live";
    public const string Failed = "failed";
    public const string Cancelled = "cancelled";
    public const string Expired = "expired";
}

/// <summary>
/// Scheduled page releases (spec 032 R2 T025, US6 scenario 4, FR-053). A publisher schedules the draft to go live, and
/// optionally to expire, at tenant-local times. The definition is kept as scheduled; at its time it is validated again
/// as the system, and a release with blocking findings does not go live: the release in force stays and the publisher is
/// notified. At the expiry the release in force before returns as a new release, or the page goes offline when it had
/// none; a page published again meanwhile is left as it is. Pages under an approval route go live through approval.
/// </summary>
public sealed class PageSchedules(
    IDbSession session,
    ITenantContext context,
    IEntitlementService entitlements,
    ITenantSettingsReader settings,
    PageService pages,
    PageRoutes routes,
    IAuditWriter audit,
    INotificationSender notifications,
    IClock clock)
{
    public const string FailedTemplate = "experience.page_schedule_failed";

    private sealed record Row(Guid Id, Guid PageId, string Definition, string? Reason, DateTimeOffset GoLiveAt, DateTimeOffset? ExpiresAt, string Status,
        Guid ScheduledBy, DateTimeOffset ScheduledAt, int? BaseRelease, int? Release, string? Findings);

    private const string Select =
        "select id, page_id, definition::text, reason, go_live_at, expires_at, status, scheduled_by, scheduled_at, base_release, release, findings::text from experience.page_schedule";

    private static Row Read(Npgsql.NpgsqlDataReader r) => new(r.GetGuid(0), r.GetGuid(1), r.GetString(2), r.Get<string?>("reason"),
        r.GetFieldValue<DateTimeOffset>(4), r.Get<DateTimeOffset?>("expires_at"), r.GetString(6), r.GetGuid(7), r.GetFieldValue<DateTimeOffset>(8),
        r.Get<int?>("base_release"), r.Get<int?>("release"), r.Get<string?>("findings"));

    private async Task<PageScheduleView> ViewAsync(Row row, CancellationToken ct) =>
        new(row.Id, row.Status, row.GoLiveAt, row.ExpiresAt, (await settings.GetLocaleSettingsAsync(ct)).TimeZone, row.Reason, row.ScheduledBy,
            row.ScheduledAt, row.Release, row.Findings is null ? [] : JsonSerializer.Deserialize<List<string>>(row.Findings)!);

    public async Task<PageScheduleState> StateAsync(Guid pageId, CancellationToken ct) =>
        new((await settings.GetLocaleSettingsAsync(ct)).TimeZone, await GetAsync(pageId, ct));

    /// <summary>The waiting schedule of the page, else the one waiting to expire, else the last one; null for none.</summary>
    private async Task<PageScheduleView?> GetAsync(Guid pageId, CancellationToken ct)
    {
        await entitlements.DemandAsync(Capabilities.ExperiencePageBuilder, ct);
        await pages.EditableInfoAsync(pageId, Authorization.Contracts.Permissions.PageEdit, ct);
        var row = await session.QuerySingleOrDefaultAsync(Select +
            " where page_id = @pageId order by (status = 'scheduled') desc, (status = 'live' and expires_at is not null) desc, scheduled_at desc limit 1",
            new { pageId }, Read, ct);
        return row is null ? null : await ViewAsync(row, ct);
    }

    public async Task<PageScheduleView> ScheduleAsync(Guid pageId, SchedulePageRequest request, CancellationToken ct)
    {
        await entitlements.DemandAsync(Capabilities.ExperiencePageBuilder, ct);
        var page = await pages.PublishableInfoAsync(pageId, ct);
        if (await routes.ForPageAsync(page.OrgUnitId, ct) is not null)
        {
            throw AppException.Conflict("conflict.page.approval_required");
        }
        var zone = TimeZoneInfo.FindSystemTimeZoneById((await settings.GetLocaleSettingsAsync(ct)).TimeZone);
        var now = clock.UtcNow;
        var errors = new ValidationErrors();
        // A local time skipped by a daylight saving change does not exist, and is refused like a time in the past.
        DateTimeOffset? goLive = request.GoLiveAt is { } g && !zone.IsInvalidTime(g) ? Utc(g, zone) : null;
        DateTimeOffset? expires = request.ExpiresAt is { } e ? zone.IsInvalidTime(e) ? DateTimeOffset.MinValue : Utc(e, zone) : null;
        if (goLive is not { } at || at <= now)
        {
            errors.Add("goLiveAt", "validation.page.schedule_time");
        }
        if (expires is { } x && goLive is { } start && x <= start)
        {
            errors.Add("expiresAt", "validation.page.schedule_expiry");
        }
        if (request.Reason is { Length: > 500 })
        {
            errors.Add("reason", "validation.page.reason_too_long");
        }
        errors.ThrowIfAny();
        if (await session.ScalarAsync<bool>(
            "select exists (select 1 from experience.page_schedule where page_id = @pageId and (status = 'scheduled' or (status = 'live' and expires_at > @now)))",
            new { pageId, now }, ct))
        {
            throw AppException.Conflict("conflict.page.scheduled");
        }
        var (_, draft) = await pages.DraftOfAsync(pageId, ct);
        PageService.Block(await pages.PublishFindingsAsync(pageId, draft, ct));
        var user = context.RequireUser();
        var id = Ids.New();
        await session.ExecAsync(
            """
            insert into experience.page_schedule (tenant_id, id, page_id, definition, reason, go_live_at, expires_at, status, scheduled_by, scheduled_at)
            values (platform.current_tenant_id(), @id, @pageId, @d, @reason, @goLive, @expires, 'scheduled', @user, @now)
            """, new { id, pageId, d = Jsonb.From(draft), reason = request.Reason, goLive = goLive!.Value, expires, user, now }, ct);
        await audit.WriteAsync(new AuditEntry(AuditActions.PageScheduled, "page", pageId.ToString(), TargetOrgUnitId: page.OrgUnitId,
            Changes: new { schedule = id, goLiveAt = goLive, expiresAt = expires, reason = request.Reason }), ct);
        return (await GetAsync(pageId, ct))!;
    }

    /// <summary>Cancels the waiting schedule, or the expiry of a live one (the page then stays as it is).</summary>
    public async Task CancelAsync(Guid pageId, CancellationToken ct)
    {
        await entitlements.DemandAsync(Capabilities.ExperiencePageBuilder, ct);
        var page = await pages.PublishableInfoAsync(pageId, ct);
        var now = clock.UtcNow;
        var row = await session.QuerySingleOrDefaultAsync(Select +
            " where page_id = @pageId and (status = 'scheduled' or (status = 'live' and expires_at > @now)) for update", new { pageId, now }, Read, ct)
            ?? throw AppException.Conflict("conflict.page.not_scheduled");
        if (row.Status == PageScheduleStatuses.Scheduled)
        {
            await session.ExecAsync("update experience.page_schedule set status = 'cancelled', ended_at = @now where id = @id", new { id = row.Id, now }, ct);
        }
        else
        {
            await session.ExecAsync("update experience.page_schedule set expires_at = null where id = @id", new { id = row.Id }, ct);
        }
        await audit.WriteAsync(new AuditEntry(AuditActions.PageScheduleCancelled, "page", pageId.ToString(), TargetOrgUnitId: page.OrgUnitId,
            Changes: new { schedule = row.Id, part = row.Status == PageScheduleStatuses.Scheduled ? "go_live" : "expiry" }), ct);
    }

    /// <summary>Puts due schedules live and ends due expiries for the session tenant, as the system. Returns how many moved.</summary>
    public async Task<int> RunDueAsync(DateTimeOffset now, int limit, CancellationToken ct)
    {
        var moved = 0;
        foreach (var id in await session.QueryAsync(
            "select id from experience.page_schedule where status = 'scheduled' and go_live_at <= @now order by go_live_at, id limit @limit",
            new { now, limit }, r => r.GetGuid(0), ct))
        {
            if (await session.QuerySingleOrDefaultAsync(Select + " where id = @id and status = 'scheduled' for update", new { id }, Read, ct) is { } row)
            {
                await GoLiveAsync(row, now, ct);
                moved++;
            }
        }
        foreach (var id in await session.QueryAsync(
            "select id from experience.page_schedule where status = 'live' and expires_at <= @now order by expires_at, id limit @limit",
            new { now, limit }, r => r.GetGuid(0), ct))
        {
            if (await session.QuerySingleOrDefaultAsync(Select + " where id = @id and status = 'live' for update", new { id }, Read, ct) is { } row)
            {
                await ExpireAsync(row, now, ct);
                moved++;
            }
        }
        return moved;
    }

    private async Task GoLiveAsync(Row row, DateTimeOffset now, CancellationToken ct)
    {
        var definition = JsonSerializer.Deserialize<PageDefinition>(row.Definition, JsonDefaults.Options)!;
        var page = await pages.FindAsync(row.PageId, ct);
        var blocking = page is null ? [] : (await pages.GoLiveFindingsAsync(row.PageId, definition, ct)).Where(f => f.Severity == Severities.Blocking).ToList();
        if (page is null || blocking.Count > 0)
        {
            // The release in force stays; the publisher learns why.
            var codes = blocking.Select(f => f.Code).Distinct().ToList();
            await session.ExecAsync("update experience.page_schedule set status = 'failed', findings = @f, ended_at = @now where id = @id",
                new { id = row.Id, f = Jsonb.From(codes), now }, ct);
            await audit.WriteAsync(new AuditEntry(AuditActions.PageScheduleFailed, "page", row.PageId.ToString(), TargetOrgUnitId: page?.OrgUnitId,
                Changes: new { schedule = row.Id, findings = codes }), ct);
            if (page is not null)
            {
                await notifications.SendAsync(new NotificationRequest([row.ScheduledBy], NotificationCategories.System, FailedTemplate, $"/admin/pages/{page.Id}",
                    new Dictionary<string, string> { ["page"] = page.Slug }, DedupeKey: $"page_schedule:{row.Id:N}:failed"), ct);
            }
            return;
        }
        var release = await pages.PublishScheduledAsync(row.PageId, definition, row.Reason, row.ScheduledBy, row.Id, ct);
        await session.ExecAsync("update experience.page_schedule set status = 'live', base_release = @b, release = @r where id = @id",
            new { id = row.Id, b = page.CurrentRelease, r = release.Release }, ct);
    }

    private async Task ExpireAsync(Row row, DateTimeOffset now, CancellationToken ct)
    {
        var page = await pages.FindAsync(row.PageId, ct);
        // A page published again since the scheduled release went live keeps that later release.
        if (page is not null && page.CurrentRelease == row.Release)
        {
            await pages.ExpireScheduledAsync(row.PageId, row.BaseRelease, row.ScheduledBy, row.Id, ct);
        }
        await session.ExecAsync("update experience.page_schedule set status = 'expired', ended_at = @now where id = @id", new { id = row.Id, now }, ct);
    }

    private static DateTimeOffset Utc(DateTime local, TimeZoneInfo zone) =>
        new(TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(local, DateTimeKind.Unspecified), zone), TimeSpan.Zero);
}
