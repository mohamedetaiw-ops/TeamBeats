using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using TeamBeats.Audit.Contracts;
using TeamBeats.Authorization.Contracts;
using TeamBeats.Entitlements.Contracts;
using TeamBeats.Messaging;
using TeamBeats.Modules.Evaluation.Contracts;
using TeamBeats.Modules.Search.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Primitives;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.Search;

/// <summary>
/// The retrieval configuration (spec 012 plan "Ranking"): passage size in tokens with the overlap between neighbours,
/// the number of passages a retrieval returns by default, and the embedding model (null lets the AI gateway route
/// choose). Version 0 is the platform default a tenant uses until a change applies.
/// </summary>
public sealed record RetrievalSettings(int Version, int PassageTokens, int OverlapPercent, int TopK, string? EmbeddingModel)
{
    public const int CharsPerToken = 4;

    public static readonly RetrievalSettings Default = new(0, 400, 15, ISearchRetrieval.DefaultTopK, null);

    public int PassageChars => PassageTokens * CharsPerToken;

    public int OverlapChars => PassageChars * OverlapPercent / 100;
}

public static class RetrievalChangeStatuses
{
    public const string Building = "building";
    public const string Gating = "gating";
    public const string Held = "held";
    public const string Active = "active";
    public const string Rejected = "rejected";
    public const string Cancelled = "cancelled";
    public const string Superseded = "superseded";

    public static readonly IReadOnlyList<string> Open = [Building, Gating, Held];
}

/// <summary>Gate results as spec 034 reports them.</summary>
public static class RetrievalGateResults
{
    public const string Passed = "passed";
    public const string Failed = "failed";
    public const string Incomplete = "incomplete";
    public const string FailedCritical = "failed_critical";
}

public sealed record RetrievalChangeRequest(int? PassageTokens, int? OverlapPercent, int? TopK, string? EmbeddingModel);

public sealed record RetrievalApplyRequest(string? Reason);

/// <summary>A configuration change with its gate. <see cref="Failures"/> are the gate's findings, "retrieval/metric:locale".</summary>
public sealed record RetrievalChangeView(Guid Id, RetrievalSettings Settings, string Status, Guid? ProposedBy, DateTimeOffset ProposedAt, DateTimeOffset? BuiltAt,
    int? ShadowUnits, string? GateResult, IReadOnlyList<string> Failures, DateTimeOffset? DecidedAt, Guid? AppliedBy, string? OverrideReason, DateTimeOffset? AppliedAt,
    bool CanApply, bool CanCancel);

/// <summary>The configuration in force, the open change if any, and earlier changes, newest first.</summary>
public sealed record RetrievalConfigView(RetrievalSettings Active, RetrievalChangeView? Open, IReadOnlyList<RetrievalChangeView> History);

/// <summary>An open change as the indexer and the gate retrieval read it.</summary>
internal sealed record OpenRetrievalChange(Guid Id, string Status, RetrievalSettings Settings);

/// <summary>Reads the configuration in force and the open change, with no permission check, for the indexer and retrieval.</summary>
public sealed class RetrievalStore(IDbSession session)
{
    private const string Columns = "version, passage_tokens, overlap_percent, top_k, embedding_model";

    private static RetrievalSettings Map(Npgsql.NpgsqlDataReader r) =>
        new(r.GetInt32(0), r.GetInt32(1), r.GetInt32(2), r.GetInt32(3), r.Get<string?>("embedding_model"));

    public async Task<RetrievalSettings> ActiveAsync(CancellationToken ct)
    {
        var rows = await session.QueryAsync($"select {Columns} from search.retrieval_config where status = 'active'", null, Map, ct);
        return rows.Count == 0 ? RetrievalSettings.Default : rows[0];
    }

    internal async Task<OpenRetrievalChange?> OpenAsync(CancellationToken ct)
    {
        var rows = await session.QueryAsync(
            $"select {Columns}, id, status from search.retrieval_config where status = any(@open)", new { open = RetrievalChangeStatuses.Open.ToArray() },
            r => new OpenRetrievalChange(r.GetGuid(5), r.GetString(6), Map(r)), ct);
        return rows.Count == 0 ? null : rows[0];
    }
}

/// <summary>
/// Retrieval configuration changes (spec 012 US3 scenario 3, FR-008), managed with <c>search.manage</c>. A change builds
/// a shadow index with its settings, which the indexer keeps in step while the change is open, then asks the spec 034
/// release gate. A passed gate applies it; a critical failure rejects it; blocking failures or a gate that could not
/// complete hold it until an administrator applies it with a reason or cancels it. Applying copies the shadow index into
/// the live one in one transaction, so search never sees a half built index.
/// </summary>
public sealed partial class RetrievalConfigs(IDbSession session, ITenantContext context, IAuthorizationService authz, IEntitlementService entitlements,
    RetrievalStore store, IOutbox outbox, IAuditWriter audit, IClock clock, ILogger<RetrievalConfigs> logger)
{
    public const int MinReason = 10;
    public const int MaxReason = 1000;

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._:/-]{0,99}$")]
    private static partial Regex ModelPattern();

    private async Task DemandAsync(CancellationToken ct)
    {
        await entitlements.DemandAsync(Capabilities.SearchCore, ct);
        await authz.DemandAsync(Permissions.SearchManage, AuthzTarget.Tenant, ct);
    }

    private const string ChangeColumns =
        """
        id, version, passage_tokens, overlap_percent, top_k, embedding_model, status, proposed_by, proposed_at, built_at, shadow_units, gate_result,
        gate_failures::text, decided_at, applied_by, override_reason, applied_at
        """;

    private static RetrievalChangeView MapChange(Npgsql.NpgsqlDataReader r)
    {
        var status = r.GetString(6);
        var gate = r.Get<string?>("gate_result");
        return new RetrievalChangeView(r.GetGuid(0), new RetrievalSettings(r.GetInt32(1), r.GetInt32(2), r.GetInt32(3), r.GetInt32(4), r.Get<string?>("embedding_model")),
            status, r.Get<Guid?>("proposed_by"), r.GetFieldValue<DateTimeOffset>(8), r.Get<DateTimeOffset?>("built_at"), r.Get<int?>("shadow_units"), gate,
            JsonSerializer.Deserialize<List<string>>(r.GetString(12)) ?? [], r.Get<DateTimeOffset?>("decided_at"), r.Get<Guid?>("applied_by"),
            r.Get<string?>("override_reason"), r.Get<DateTimeOffset?>("applied_at"),
            status == RetrievalChangeStatuses.Held && gate is RetrievalGateResults.Failed or RetrievalGateResults.Incomplete,
            RetrievalChangeStatuses.Open.Contains(status));
    }

    public async Task<RetrievalConfigView> GetAsync(CancellationToken ct)
    {
        await DemandAsync(ct);
        var changes = await session.QueryAsync($"select {ChangeColumns} from search.retrieval_config order by version desc limit 20", null, MapChange, ct);
        return new RetrievalConfigView(await store.ActiveAsync(ct), changes.FirstOrDefault(c => RetrievalChangeStatuses.Open.Contains(c.Status)),
            changes.Where(c => !RetrievalChangeStatuses.Open.Contains(c.Status)).ToList());
    }

    /// <summary>Proposes a change: unset fields keep the value in force, and at least one must differ from it.</summary>
    public async Task<RetrievalConfigView> ProposeAsync(RetrievalChangeRequest request, CancellationToken ct)
    {
        await DemandAsync(ct);
        var by = context.RequireUser();
        var active = await store.ActiveAsync(ct);
        var errors = new ValidationErrors();
        if (request.PassageTokens is < 100 or > 1000)
        {
            errors.Add("passageTokens", "validation.search.passage_tokens");
        }
        if (request.OverlapPercent is < 0 or > 50)
        {
            errors.Add("overlapPercent", "validation.search.overlap");
        }
        if (request.TopK is < 1 or > ISearchRetrieval.MaxTopK)
        {
            errors.Add("topK", "validation.search.top_k");
        }
        var model = request.EmbeddingModel is null ? active.EmbeddingModel : request.EmbeddingModel.Trim() is { Length: > 0 } m ? m : null;
        if (model is not null && !ModelPattern().IsMatch(model))
        {
            errors.Add("embeddingModel", "validation.search.embedding_model");
        }
        errors.ThrowIfAny();
        var next = new RetrievalSettings(0, request.PassageTokens ?? active.PassageTokens, request.OverlapPercent ?? active.OverlapPercent, request.TopK ?? active.TopK, model);
        if (next with { Version = active.Version } == active)
        {
            throw AppException.Validation("passageTokens", "validation.search.retrieval_unchanged");
        }
        if (await store.OpenAsync(ct) is not null)
        {
            throw AppException.Conflict("conflict.search.retrieval_change_open");
        }
        var id = Ids.New();
        var version = await session.ScalarAsync<int>("select coalesce(max(version), 0)::int + 1 from search.retrieval_config", null, ct);
        await session.ExecAsync(
            """
            insert into search.retrieval_config (tenant_id, id, version, passage_tokens, overlap_percent, top_k, embedding_model, status, proposed_by, proposed_at)
            values (platform.current_tenant_id(), @id, @version, @tokens, @overlap, @topK, @model, 'building', @by, @now)
            """, new { id, version, tokens = next.PassageTokens, overlap = next.OverlapPercent, topK = next.TopK, model = next.EmbeddingModel, by, now = clock.UtcNow }, ct);
        await audit.WriteAsync(new AuditEntry(AuditActions.SearchRetrievalChangeProposed, "search_retrieval_change", id.ToString(),
            Changes: new { version, before = active, after = next with { Version = version } }), ct);
        await outbox.EnqueueAsync(new RetrievalShadowBuildRequested(id), ct);
        logger.LogInformation("search.retrieval_change_proposed tenant {Tenant} change {Change} version {Version}", context.TenantId, id, version);
        return await GetAsync(ct);
    }

    /// <summary>Applies a held change with a reason after the gate reported blocking failures or could not complete (FR-008, spec 034 FR-017).</summary>
    public async Task<RetrievalConfigView> ApplyAsync(Guid id, RetrievalApplyRequest request, CancellationToken ct)
    {
        await DemandAsync(ct);
        var me = context.RequireUser();
        var reason = request.Reason?.Trim() ?? string.Empty;
        if (reason.Length is < MinReason or > MaxReason)
        {
            throw AppException.Validation("reason", "validation.search.override_reason");
        }
        var change = await LockAsync(id, ct);
        if (change.Status == RetrievalChangeStatuses.Held && change.GateResult is RetrievalGateResults.FailedCritical)
        {
            throw AppException.Conflict("conflict.search.retrieval_gate_critical");
        }
        if (!change.CanApply)
        {
            throw AppException.Conflict(RetrievalChangeStatuses.Open.Contains(change.Status) ? "conflict.search.retrieval_gate_running" : "conflict.search.retrieval_change_decided");
        }
        await session.ExecAsync("update search.retrieval_config set applied_by = @me, override_reason = @reason where id = @id", new { id, me, reason }, ct);
        await audit.WriteAsync(new AuditEntry(AuditActions.SearchRetrievalChangeOverridden, "search_retrieval_change", id.ToString(),
            Changes: new { version = change.Settings.Version, gate = change.GateResult, failures = change.Failures, reason }), ct);
        await SwapAsync(id, change.Settings.Version, ct);
        return await GetAsync(ct);
    }

    /// <summary>Withdraws an open change and empties its shadow index; the configuration in force stays.</summary>
    public async Task<RetrievalConfigView> CancelAsync(Guid id, CancellationToken ct)
    {
        await DemandAsync(ct);
        var change = await LockAsync(id, ct);
        if (!change.CanCancel)
        {
            throw AppException.Conflict("conflict.search.retrieval_change_decided");
        }
        await session.ExecAsync("update search.retrieval_config set status = 'cancelled', decided_at = @now where id = @id", new { id, now = clock.UtcNow }, ct);
        await session.ExecAsync("delete from search.shadow_unit", null, ct);
        await audit.WriteAsync(new AuditEntry(AuditActions.SearchRetrievalChangeCancelled, "search_retrieval_change", id.ToString(),
            Changes: new { version = change.Settings.Version }), ct);
        return await GetAsync(ct);
    }

    private async Task<RetrievalChangeView> LockAsync(Guid id, CancellationToken ct)
    {
        var rows = await session.QueryAsync($"select {ChangeColumns} from search.retrieval_config where id = @id for update", new { id }, MapChange, ct);
        return rows.Count == 0 ? throw AppException.NotFound() : rows[0];
    }

    /// <summary>
    /// Takes the gate's decision (spec 034 section 13). A change no longer waiting for its gate is left as it is, so a
    /// repeated or late message changes nothing.
    /// </summary>
    internal async Task DecideAsync(Guid id, string result, IReadOnlyList<string> failures, CancellationToken ct)
    {
        var rows = await session.QueryAsync("select status, version from search.retrieval_config where id = @id for update", new { id },
            r => (Status: r.GetString(0), Version: r.GetInt32(1)), ct);
        if (rows.Count == 0 || rows[0].Status != RetrievalChangeStatuses.Gating)
        {
            return;
        }
        var version = rows[0].Version;
        var now = clock.UtcNow;
        var listed = Jsonb.From(failures.Take(100).ToList());
        var gate = result switch
        {
            RetrievalGateResults.Passed => RetrievalGateResults.Passed,
            RetrievalGateResults.FailedCritical => RetrievalGateResults.FailedCritical,
            RetrievalGateResults.Failed => RetrievalGateResults.Failed,
            _ => RetrievalGateResults.Incomplete,
        };
        await session.ExecAsync("update search.retrieval_config set gate_result = @gate, gate_failures = @listed, decided_at = @now where id = @id",
            new { id, gate, listed, now }, ct);
        await audit.WriteAsync(new AuditEntry(AuditActions.SearchRetrievalChangeDecided, "search_retrieval_change", id.ToString(),
            Changes: new { version, gate, failures = failures.Take(100) }), ct);
        logger.LogInformation("search.retrieval_gate_decided tenant {Tenant} change {Change} version {Version} result {Result}", context.TenantId, id, version, gate);
        switch (gate)
        {
            case RetrievalGateResults.Passed:
                await SwapAsync(id, version, ct);
                break;
            case RetrievalGateResults.FailedCritical:
                await session.ExecAsync("update search.retrieval_config set status = 'rejected' where id = @id", new { id }, ct);
                await session.ExecAsync("delete from search.shadow_unit", null, ct);
                break;
            default:
                await session.ExecAsync("update search.retrieval_config set status = 'held' where id = @id", new { id }, ct);
                break;
        }
    }

    /// <summary>The shadow index becomes the live one and the change the configuration in force, in the caller's transaction.</summary>
    private async Task SwapAsync(Guid id, int version, CancellationToken ct)
    {
        const string columns =
            "tenant_id, id, source, item_id, unit_key, position, kind, locale, title, body, link, classification, org_unit_id, version, updated_at, synced_at, tsv, embedding, embedding_dims, embedding_model";
        await session.ExecAsync("delete from search.unit", null, ct);
        var units = await session.ExecAsync($"insert into search.unit ({columns}) select {columns} from search.shadow_unit", null, ct);
        await session.ExecAsync("delete from search.shadow_unit", null, ct);
        var now = clock.UtcNow;
        await session.ExecAsync("update search.retrieval_config set status = 'superseded' where status = 'active'", null, ct);
        await session.ExecAsync("update search.retrieval_config set status = 'active', applied_at = @now where id = @id", new { id, now }, ct);
        logger.LogInformation("search.retrieval_change_applied tenant {Tenant} change {Change} version {Version} units {Units}", context.TenantId, id, version, units);
    }
}

/// <summary>
/// Builds the shadow index of a proposed change from every owner, as a rebuild does (FR-012), then asks the release
/// gate. Items that change while it builds are written to the shadow index by the indexer too.
/// </summary>
public sealed class RetrievalShadowBuilder(IDbSession session, IEnumerable<ISearchSource> sources, SearchIndexer indexer, RetrievalStore store, IOutbox outbox,
    IClock clock, ITenantContext context, ILogger<RetrievalShadowBuilder> logger) : IMessageHandler<RetrievalShadowBuildRequested>
{
    public async Task HandleAsync(RetrievalShadowBuildRequested message, CancellationToken ct)
    {
        if (await store.OpenAsync(ct) is not { Status: RetrievalChangeStatuses.Building } open || open.Id != message.ChangeId)
        {
            return;
        }
        var started = clock.UtcNow;
        await session.ExecAsync("delete from search.shadow_unit", null, ct);
        int items = 0, units = 0;
        foreach (var source in sources)
        {
            Guid? after = null;
            while (true)
            {
                var ids = await source.ItemIdsAsync(after, SearchRebuilds.PageSize, ct);
                foreach (var id in ids)
                {
                    units += await indexer.IndexShadowAsync(source.Source, id, open.Settings, ct);
                    items++;
                }
                if (ids.Count < SearchRebuilds.PageSize)
                {
                    break;
                }
                after = ids[^1];
            }
        }
        var proposer = await session.ScalarAsync<Guid?>("select proposed_by from search.retrieval_config where id = @id", new { id = open.Id }, ct);
        await session.ExecAsync("update search.retrieval_config set status = 'gating', built_at = @now, shadow_units = @units where id = @id",
            new { id = open.Id, now = clock.UtcNow, units }, ct);
        await outbox.EnqueueAsync(new RetrievalConfigChangeProposed(open.Id, open.Settings.Version, proposer), ct);
        logger.LogInformation("search.retrieval_shadow_built tenant {Tenant} change {Change} items {Items} units {Units} in {Seconds}s", context.TenantId, open.Id, items,
            units, (clock.UtcNow - started).TotalSeconds);
    }
}

/// <summary>Takes the release gate's decision on a retrieval change (spec 034 section 13).</summary>
public sealed class RetrievalGateDecisions(RetrievalConfigs configs) : IMessageHandler<EvaluationRetrievalGateDecided>
{
    public Task HandleAsync(EvaluationRetrievalGateDecided message, CancellationToken ct) => configs.DecideAsync(message.ChangeId, message.Result, message.Failures, ct);
}
