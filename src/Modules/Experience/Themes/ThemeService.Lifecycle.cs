using System.Text.Json;
using TeamBeats.Audit.Contracts;
using TeamBeats.Authorization.Contracts;
using TeamBeats.Modules.Experience.Contracts;
using TeamBeats.Modules.Notifications.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Primitives;

namespace TeamBeats.Modules.Experience.Themes;

/// <param name="HasDraft">False when no draft is kept: the fields then show the version in force, ready to edit.</param>
/// <param name="Stale">The draft started from a version that is no longer the one in force.</param>
public sealed record BrandDraftView(Guid BrandId, string Name, IReadOnlyDictionary<string, TokenValue> Tokens, IReadOnlyDictionary<string, string> Assets,
    bool StrictContrast, int BaseVersion, int CurrentVersion, bool HasDraft, bool Stale, Guid? UpdatedBy, DateTimeOffset? UpdatedAt,
    IReadOnlyList<ContrastFinding> Findings);

public sealed record PublishBrandRequest(string? Reason);

public sealed record RevertBrandRequest(int? Version, string? Reason);

/// <param name="How">direct, approved, scheduled or reverted; null for a version published on save before R2.</param>
public sealed record BrandReleaseView(int Version, bool Current, Guid? PublishedBy, DateTimeOffset PublishedAt, string? Reason, int? RollbackOf, string? How);

/// <param name="GoLiveAt">Tenant-local date and time, without an offset.</param>
public sealed record ScheduleBrandRequest(DateTime? GoLiveAt, string? Reason);

public sealed record BrandScheduleView(Guid Id, string Status, DateTimeOffset GoLiveAt, string TimeZone, string? Reason, Guid ScheduledBy,
    DateTimeOffset ScheduledAt, int? Version, string? Failure);

/// <param name="Current">The waiting schedule, else the last one; null for none.</param>
public sealed record BrandScheduleState(string TimeZone, BrandScheduleView? Current);

/// <summary>A brand version about to go live: the draft, a submission or a schedule, as it was kept.</summary>
internal sealed record BrandCandidate(string Name, Dictionary<string, TokenValue> Tokens, Dictionary<string, string> Assets, bool Strict);

/// <summary>
/// Theme lifecycle (spec 033 R2 T014, US2, FR-012). Each brand keeps one working draft that employees never see: the resolver
/// reads only the version in force. A publisher publishes the draft as the next immutable version, or schedules it; an editor
/// who does not publish submits it for approval (<see cref="ThemeApprovals"/>). Every published version records who published
/// it, how and why. A revert publishes an earlier version again as a new one, so nothing is rewritten, and it touches only
/// tokens and assets: page placements and personal layouts never change. Contrast is checked again whenever a version goes live.
/// </summary>
public sealed partial class ThemeService
{
    public const int MaxReasonLength = 500;
    public const string ScheduleFailedTemplate = "experience.theme_schedule_failed";

    private const string DraftSelect = "select name, tokens::text, assets::text, strict_contrast, base_version, updated_by, updated_at from experience.brand_draft";

    public static class ScheduleStatuses
    {
        public const string Scheduled = "scheduled";
        public const string Live = "live";
        public const string Failed = "failed";
        public const string Cancelled = "cancelled";
    }

    private static Dictionary<string, TokenValue> Tokens(string json) => JsonSerializer.Deserialize<Dictionary<string, TokenValue>>(json, JsonDefaults.Options)!;

    private static Dictionary<string, string> Assets(string json) => JsonSerializer.Deserialize<Dictionary<string, string>>(json, JsonDefaults.Options)!;

    private async Task<int> CurrentVersionAsync(Guid id, bool forUpdate, CancellationToken ct) =>
        await session.QuerySingleOrDefaultAsync("select current_version from experience.brand where id = @id" + (forUpdate ? " for update" : ""),
            new { id }, r => (int?)r.GetInt32(0), ct) ?? throw AppException.NotFound();

    /// <summary>The version in force as a candidate, for the approval route to compare and show. Null when there is no such brand.</summary>
    internal async Task<BrandCandidate?> VersionInForceAsync(Guid id, CancellationToken ct) =>
        await BrandAsync(id, null, ct) is { } b ? new BrandCandidate(b.Name, b.Tokens, b.Assets, b.Strict) : null;

    internal Task<int> CurrentVersionOfAsync(Guid id, CancellationToken ct) => CurrentVersionAsync(id, false, ct);

    internal static IReadOnlyList<ContrastFinding> FindingsOf(BrandCandidate candidate) => CandidateFindings(candidate.Tokens, candidate.Strict);

    private static string? Reason(string? reason)
    {
        var trimmed = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim();
        return trimmed is { Length: > MaxReasonLength } ? throw AppException.Validation("reason", "validation.theme.reason_too_long") : trimmed;
    }

    /// <summary>The draft, or the version in force when there is none, for someone who edits themes.</summary>
    public async Task<BrandDraftView> GetDraftAsync(Guid id, CancellationToken ct)
    {
        await authz.DemandAsync(Permissions.ThemeEdit, AuthzTarget.Tenant, ct);
        await EnsureSeededAsync(ct);
        var brand = await BrandAsync(id, null, ct) ?? throw AppException.NotFound();
        var draft = await session.QuerySingleOrDefaultAsync(DraftSelect + " where brand_id = @id", new { id },
            r => ((string Name, Dictionary<string, TokenValue> Tokens, Dictionary<string, string> Assets, bool Strict, int Base, Guid By, DateTimeOffset At)?)
                (r.GetString(0), Tokens(r.GetString(1)), Assets(r.GetString(2)), r.GetBoolean(3), r.GetInt32(4), r.GetGuid(5), r.GetFieldValue<DateTimeOffset>(6)), ct);
        if (draft is null)
        {
            return new BrandDraftView(id, brand.Name, brand.Tokens, brand.Assets, brand.Strict, brand.Version, brand.Version, false, false, null, null,
                CandidateFindings(brand.Tokens, brand.Strict));
        }
        return new BrandDraftView(id, draft.Value.Name, draft.Value.Tokens, draft.Value.Assets, draft.Value.Strict, draft.Value.Base, brand.Version, true,
            draft.Value.Base != brand.Version, draft.Value.By, draft.Value.At, CandidateFindings(draft.Value.Tokens, draft.Value.Strict));
    }

    private static IReadOnlyList<ContrastFinding> CandidateFindings(Dictionary<string, TokenValue> tokens, bool strict) =>
        ThemeEngine.Check(ThemeEngine.Resolve([new ThemeLayer(0, null, "brand", null, null, tokens)], new Dictionary<string, (int, Guid?)>()), strict);

    /// <summary>
    /// Keeps the draft. Values are typed and checked as on publish, but contrast findings that would block a publish are
    /// reported, not refused, so work in progress can be saved; they block when the draft goes live.
    /// </summary>
    public async Task<BrandDraftView> SaveDraftAsync(Guid id, BrandRequest request, CancellationToken ct)
    {
        await authz.DemandAsync(Permissions.ThemeEdit, AuthzTarget.Tenant, ct);
        var current = await CurrentVersionAsync(id, true, ct);
        var (tokens, assets) = await ValidateBrandAsync(request, ct);
        var existing = await session.QuerySingleOrDefaultAsync("select base_version from experience.brand_draft where brand_id = @id for update", new { id },
            r => (int?)r.GetInt32(0), ct);
        await session.ExecAsync(
            """
            insert into experience.brand_draft (tenant_id, brand_id, name, tokens, assets, strict_contrast, base_version, updated_by, updated_at)
            values (platform.current_tenant_id(), @id, @name, @tokens, @assets, @strict, @base, @user, @now)
            on conflict (tenant_id, brand_id) do update set name = excluded.name, tokens = excluded.tokens, assets = excluded.assets,
              strict_contrast = excluded.strict_contrast, updated_by = excluded.updated_by, updated_at = excluded.updated_at
            """, new { id, name = request.Name!.Trim(), tokens = Jsonb.From(tokens), assets = Jsonb.From(assets), strict = request.StrictContrast,
                @base = existing ?? current, user = context.RequireUser(), now = clock.UtcNow }, ct);
        if (existing is null)
        {
            await audit.WriteAsync(new AuditEntry(AuditActions.ThemeDrafted, "brand", id.ToString(), Changes: new { brand = id, baseVersion = current }), ct);
        }
        return await GetDraftAsync(id, ct);
    }

    /// <summary>Throws the draft away; the version in force is untouched.</summary>
    public async Task DiscardDraftAsync(Guid id, CancellationToken ct)
    {
        await authz.DemandAsync(Permissions.ThemeEdit, AuthzTarget.Tenant, ct);
        await CurrentVersionAsync(id, true, ct);
        await session.ExecAsync("delete from experience.brand_draft where brand_id = @id", new { id }, ct);
    }

    /// <summary>The draft as it would go live, or not found when there is none.</summary>
    internal async Task<BrandCandidate> DraftCandidateAsync(Guid id, CancellationToken ct) =>
        await session.QuerySingleOrDefaultAsync(DraftSelect + " where brand_id = @id", new { id },
            r => new BrandCandidate(r.GetString(0), Tokens(r.GetString(1)), Assets(r.GetString(2)), r.GetBoolean(3)), ct)
        ?? throw AppException.Conflict("conflict.theme.no_draft");

    /// <summary>Blocking contrast findings the candidate would have, alone and at every scope that uses the brand.</summary>
    internal async Task ThrowIfBlockedAsync(Guid id, BrandCandidate candidate, CancellationToken ct)
    {
        BrandFindings(candidate.Tokens, candidate.Strict);
        foreach (var scope in (await AssignmentsAsync(ct)).Where(a => a.BrandId == id).Select(a => a.Scope))
        {
            ThrowIfBlocking(ThemeEngine.Check(await ResolveAsync(scope, ct, brandOverride: (id, candidate.Tokens)), candidate.Strict), "tokens");
        }
    }

    /// <summary>Publishes the draft as the next version (theme.publish). A draft waiting for approval goes live through the approval.</summary>
    public async Task<BrandView> PublishDraftAsync(Guid id, PublishBrandRequest request, CancellationToken ct)
    {
        await authz.DemandAsync(Permissions.ThemePublish, AuthzTarget.Tenant, ct);
        await CurrentVersionAsync(id, true, ct);
        var reason = Reason(request.Reason);
        if (await session.ScalarAsync<bool>("select exists (select 1 from experience.brand_submission where brand_id = @id and status = 'pending')", new { id }, ct))
        {
            throw AppException.Conflict("conflict.theme.in_approval");
        }
        var candidate = await DraftCandidateAsync(id, ct);
        await PublishCandidateAsync(id, candidate, "direct", reason, null, context.RequireUser(), ct);
        await session.ExecAsync("delete from experience.brand_draft where brand_id = @id", new { id }, ct);
        return await GetBrandAsync(id, ct);
    }

    /// <summary>
    /// Puts a candidate live as the next immutable version, after the contrast checks. Used by publish, approval, the schedule
    /// and revert; the caller has checked who may do it.
    /// </summary>
    internal async Task<int> PublishCandidateAsync(Guid id, BrandCandidate candidate, string how, string? reason, int? rollbackOf, Guid? publisher,
        CancellationToken ct)
    {
        var current = await CurrentVersionAsync(id, true, ct);
        await ThrowIfBlockedAsync(id, candidate, ct);
        var version = current + 1;
        await InsertVersionAsync(id, version, candidate.Tokens, candidate.Assets, candidate.Strict, publisher, ct);
        await session.ExecAsync("update experience.brand set name = @name, current_version = @v where id = @id", new { id, v = version, name = candidate.Name }, ct);
        await InsertReleaseAsync(id, version, publisher, how, reason, rollbackOf, ct);
        await PublishedAsync(id, version, current, ct, how, reason);
        return version;
    }

    private Task InsertReleaseAsync(Guid id, int version, Guid? publisher, string how, string? reason, int? rollbackOf, CancellationToken ct) =>
        session.ExecAsync(
            """
            insert into experience.brand_release (tenant_id, brand_id, version, published_by, published_at, reason, rollback_of, how)
            values (platform.current_tenant_id(), @id, @version, @publisher, @now, @reason, @rollbackOf, @how)
            """, new { id, version, publisher, now = clock.UtcNow, reason, rollbackOf, how }, ct);

    /// <summary>Every version of the brand, newest first, with how it went live.</summary>
    public async Task<IReadOnlyList<BrandReleaseView>> ReleasesAsync(Guid id, CancellationToken ct)
    {
        await authz.DemandAsync(Permissions.ThemeEdit, AuthzTarget.Tenant, ct);
        var current = await CurrentVersionAsync(id, false, ct);
        return await session.QueryAsync(
            """
            select v.version, coalesce(r.published_by, v.created_by) as published_by, coalesce(r.published_at, v.created_at) as published_at, r.reason,
              r.rollback_of, r.how
            from experience.brand_version v left join experience.brand_release r on r.brand_id = v.brand_id and r.version = v.version
            where v.brand_id = @id order by v.version desc
            """, new { id },
            r => new BrandReleaseView(r.GetInt32(0), r.GetInt32(0) == current, r.Get<Guid?>("published_by"), r.GetFieldValue<DateTimeOffset>(2),
                r.Get<string>("reason"), r.Get<int?>("rollback_of"), r.Get<string>("how")), ct);
    }

    /// <summary>
    /// Brings an earlier identity back by publishing that version again as a new one (US2 scenario 5). Pages, their placements
    /// and personal layouts are never part of a theme, so they stay as they are. A kept draft stays a draft.
    /// </summary>
    public async Task<BrandView> RevertAsync(Guid id, RevertBrandRequest request, CancellationToken ct)
    {
        await authz.DemandAsync(Permissions.ThemePublish, AuthzTarget.Tenant, ct);
        var current = await CurrentVersionAsync(id, true, ct);
        var reason = Reason(request.Reason);
        if (request.Version is not { } target || target == current)
        {
            throw AppException.Validation("version", "validation.theme.revert_version");
        }
        var old = await BrandAsync(id, target, ct) ?? throw AppException.Validation("version", "validation.theme.revert_version");
        var version = await PublishCandidateAsync(id, new BrandCandidate(old.Name, old.Tokens, old.Assets, old.Strict), "reverted", reason, target,
            context.RequireUser(), ct);
        await audit.WriteAsync(new AuditEntry(AuditActions.ThemeRolledBack, "brand", id.ToString(),
            Changes: new { brand = id, version, restored = target, previousVersion = current, reason }), ct);
        await outbox.EnqueueAsync(new ThemeRolledBack(id, version, target), ct);
        return await GetBrandAsync(id, ct);
    }

    // ---- Scheduled publish ----

    private const string ScheduleSelect =
        "select id, brand_id, name, tokens::text, assets::text, strict_contrast, go_live_at, status, reason, scheduled_by, scheduled_at, version, failure from experience.brand_schedule";

    private sealed record ScheduleRow(Guid Id, Guid BrandId, BrandCandidate Candidate, DateTimeOffset GoLiveAt, string Status, string? Reason, Guid ScheduledBy,
        DateTimeOffset ScheduledAt, int? Version, string? Failure);

    private static ScheduleRow ReadSchedule(Npgsql.NpgsqlDataReader r) => new(r.GetGuid(0), r.GetGuid(1),
        new BrandCandidate(r.GetString(2), Tokens(r.GetString(3)), Assets(r.GetString(4)), r.GetBoolean(5)), r.GetFieldValue<DateTimeOffset>(6), r.GetString(7),
        r.Get<string>("reason"), r.GetGuid(9), r.GetFieldValue<DateTimeOffset>(10), r.Get<int?>("version"), r.Get<string>("failure"));

    private static BrandScheduleView ScheduleView(ScheduleRow row, string zone) =>
        new(row.Id, row.Status, row.GoLiveAt, zone, row.Reason, row.ScheduledBy, row.ScheduledAt, row.Version, row.Failure);

    public async Task<BrandScheduleState> ScheduleStateAsync(Guid id, CancellationToken ct)
    {
        await authz.DemandAsync(Permissions.ThemeEdit, AuthzTarget.Tenant, ct);
        await CurrentVersionAsync(id, false, ct);
        var zone = (await tenantSettings.GetLocaleSettingsAsync(ct)).TimeZone;
        var row = await session.QuerySingleOrDefaultAsync(ScheduleSelect +
            " where brand_id = @id order by (status = 'scheduled') desc, scheduled_at desc limit 1", new { id }, ReadSchedule, ct);
        return new BrandScheduleState(zone, row is null ? null : ScheduleView(row, zone));
    }

    /// <summary>Schedules the draft, as it is now, to go live at a tenant-local time (theme.publish). It is checked again at the time.</summary>
    public async Task<BrandScheduleState> ScheduleAsync(Guid id, ScheduleBrandRequest request, CancellationToken ct)
    {
        await authz.DemandAsync(Permissions.ThemePublish, AuthzTarget.Tenant, ct);
        await CurrentVersionAsync(id, true, ct);
        var reason = Reason(request.Reason);
        var zone = TimeZoneInfo.FindSystemTimeZoneById((await tenantSettings.GetLocaleSettingsAsync(ct)).TimeZone);
        var now = clock.UtcNow;
        // A local time skipped by a daylight saving change does not exist, and is refused like a time in the past.
        DateTimeOffset? goLive = request.GoLiveAt is { } g && !zone.IsInvalidTime(g)
            ? new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(g, DateTimeKind.Unspecified), zone), TimeSpan.Zero)
            : null;
        if (goLive is not { } at || at <= now || at > now.AddYears(1))
        {
            throw AppException.Validation("goLiveAt", "validation.theme.schedule_time");
        }
        if (await session.ScalarAsync<bool>("select exists (select 1 from experience.brand_schedule where brand_id = @id and status = 'scheduled')", new { id }, ct))
        {
            throw AppException.Conflict("conflict.theme.scheduled");
        }
        if (await session.ScalarAsync<bool>("select exists (select 1 from experience.brand_submission where brand_id = @id and status = 'pending')", new { id }, ct))
        {
            throw AppException.Conflict("conflict.theme.in_approval");
        }
        var candidate = await DraftCandidateAsync(id, ct);
        await ThrowIfBlockedAsync(id, candidate, ct);
        var scheduleId = Ids.New();
        await session.ExecAsync(
            """
            insert into experience.brand_schedule (tenant_id, id, brand_id, name, tokens, assets, strict_contrast, go_live_at, status, reason, scheduled_by, scheduled_at)
            values (platform.current_tenant_id(), @scheduleId, @id, @name, @tokens, @assets, @strict, @at, 'scheduled', @reason, @user, @now)
            """, new { scheduleId, id, name = candidate.Name, tokens = Jsonb.From(candidate.Tokens), assets = Jsonb.From(candidate.Assets),
                strict = candidate.Strict, at = goLive.Value, reason, user = context.RequireUser(), now }, ct);
        await audit.WriteAsync(new AuditEntry(AuditActions.ThemeScheduled, "brand", id.ToString(),
            Changes: new { brand = id, schedule = scheduleId, goLiveAt = goLive, reason }), ct);
        return await ScheduleStateAsync(id, ct);
    }

    public async Task CancelScheduleAsync(Guid id, CancellationToken ct)
    {
        await authz.DemandAsync(Permissions.ThemePublish, AuthzTarget.Tenant, ct);
        var row = await session.QuerySingleOrDefaultAsync(ScheduleSelect + " where brand_id = @id and status = 'scheduled' for update", new { id }, ReadSchedule, ct)
            ?? throw AppException.Conflict("conflict.theme.not_scheduled");
        await session.ExecAsync("update experience.brand_schedule set status = 'cancelled' where id = @sid", new { sid = row.Id }, ct);
        await audit.WriteAsync(new AuditEntry(AuditActions.ThemeScheduled, "brand", id.ToString(), Changes: new { brand = id, schedule = row.Id, cancelled = true }), ct);
    }

    /// <summary>Publishes due schedules for the session tenant, as the system. A schedule whose theme fails the checks does not go live.</summary>
    public async Task<int> RunDueSchedulesAsync(DateTimeOffset now, int limit, CancellationToken ct)
    {
        var moved = 0;
        foreach (var sid in await session.QueryAsync(
            "select id from experience.brand_schedule where status = 'scheduled' and go_live_at <= @now order by go_live_at, id limit @limit",
            new { now, limit }, r => r.GetGuid(0), ct))
        {
            if (await session.QuerySingleOrDefaultAsync(ScheduleSelect + " where id = @sid and status = 'scheduled' for update", new { sid }, ReadSchedule, ct) is not { } row)
            {
                continue;
            }
            moved++;
            try
            {
                // Checked again at the time: a scope may have been assigned the brand since, with colors that now fail.
                await ThrowIfBlockedAsync(row.BrandId, row.Candidate, ct);
            }
            catch (AppException e)
            {
                // The version in force stays; the publisher learns why.
                var failure = e.DetailKey ?? e.Code;
                await session.ExecAsync("update experience.brand_schedule set status = 'failed', failure = @failure where id = @sid", new { sid, failure }, ct);
                await audit.WriteAsync(new AuditEntry(AuditActions.ThemeScheduled, "brand", row.BrandId.ToString(),
                    Changes: new { brand = row.BrandId, schedule = row.Id, failed = failure }), ct);
                await notifications.SendAsync(new NotificationRequest([row.ScheduledBy], NotificationCategories.System, ScheduleFailedTemplate,
                    $"/admin/branding?brand={row.BrandId}", new Dictionary<string, string> { ["brand"] = row.Candidate.Name },
                    DedupeKey: $"brand_schedule:{row.Id:N}:failed"), ct);
                continue;
            }
            var version = await PublishCandidateAsync(row.BrandId, row.Candidate, "scheduled", row.Reason, null, row.ScheduledBy, ct);
            await session.ExecAsync("update experience.brand_schedule set status = 'live', version = @version where id = @sid", new { sid, version }, ct);
        }
        return moved;
    }
}
