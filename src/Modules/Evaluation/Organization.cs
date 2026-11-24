using System.Text.Json;
using TeamBeats.Audit.Contracts;
using TeamBeats.Authorization.Contracts;
using TeamBeats.Entitlements.Contracts;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Primitives;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.Evaluation;

/// <summary>A unit's evaluation settings: stricter thresholds for it and below, and its business datasets every agent there is tested on.</summary>
public sealed record UnitSettingsRequest(int Version, List<SuiteMetric>? Metrics, List<Guid>? Datasets);

/// <summary>The settings of a unit above, inherited read-only.</summary>
public sealed record UnitLevelView(Guid UnitId, IReadOnlyDictionary<string, string>? UnitNames, int Version, IReadOnlyList<SuiteMetric> Metrics, int DatasetCount);

public sealed record UnitSettingsView(Guid UnitId, bool Available, int Version, IReadOnlyList<SuiteMetric> Metrics, IReadOnlyList<Guid> Datasets,
    IReadOnlyList<DatasetSummary> OwnDatasets, IReadOnlyList<UnitLevelView> Above, IReadOnlyList<EffectiveRule> Rules, bool CanEdit, DateTimeOffset? UpdatedAt);

/// <summary>
/// The organization level of US10 and FR-020 (R2): a unit sets thresholds stricter than the platform, the tenant and the
/// units above for itself and every unit below, and names business datasets of its own that every agent of the unit and
/// below is tested on as an inherited suite. The level applies only with advanced evaluation.
/// </summary>
public sealed class UnitEvalSettings(IDbSession session, ITenantContext context, EvalAccess access, IEntitlementService entitlements, IOrganizationHierarchy org,
    EvalSettingsService settings, IAuditWriter audit, IClock clock)
{
    public const int MaxDatasets = 20;

    internal sealed record Level(Guid UnitId, int Version, List<SuiteMetric> Metrics, Guid[] Datasets, DateTimeOffset UpdatedAt);

    /// <summary>The key of a unit's inherited suite in a run.</summary>
    public static string SuiteKey(Guid unitId) => $"org:{unitId:N}";

    /// <summary>The settings in force for a unit, from the root down to the unit itself; none without advanced evaluation.</summary>
    internal async Task<IReadOnlyList<Level>> PathAsync(Guid unitId, CancellationToken ct)
    {
        if (!await entitlements.IsEnabledAsync(Capabilities.AiEvalsAdvanced, ct))
        {
            return [];
        }
        var path = (await org.GetAncestorsAndSelfAsync(unitId, ct)).Reverse().ToList();
        var rows = await LoadAsync(path, ct);
        return path.Where(rows.ContainsKey).Select(u => rows[u]).ToList();
    }

    private async Task<Dictionary<Guid, Level>> LoadAsync(IReadOnlyList<Guid> units, CancellationToken ct) =>
        (await session.QueryAsync("select org_unit_id, version, metrics::text, datasets, updated_at from evaluation.unit_settings where org_unit_id = any(@units)",
            new { units = units.ToArray() },
            r => new Level(r.GetGuid(0), r.GetInt32(1), JsonSerializer.Deserialize<List<SuiteMetric>>(r.GetString(2), EvalJson.Options)!, r.GetFieldValue<Guid[]>(3),
                r.GetFieldValue<DateTimeOffset>(4)), ct)).ToDictionary(l => l.UnitId);

    internal static IEnumerable<(string Level, IReadOnlyDictionary<string, LevelRule> Rules)> Rules(IEnumerable<Level> levels) =>
        levels.Select(l => (EvalLevels.Organization, ThresholdRules.Rules(l.Metrics)));

    private async Task<(Guid Unit, bool Manage)> ReadableAsync(Guid unitId, CancellationToken ct)
    {
        if (!await org.UnitExistsAsync(unitId, ct) || !await access.CanReadUnitAsync(unitId, ct))
        {
            throw AppException.NotFound();
        }
        return (unitId, await access.CanManageUnitAsync(unitId, ct));
    }

    public async Task<UnitSettingsView> GetAsync(Guid unitId, CancellationToken ct)
    {
        var (_, manage) = await ReadableAsync(unitId, ct);
        return await ViewAsync(unitId, manage, ct);
    }

    private async Task<UnitSettingsView> ViewAsync(Guid unitId, bool manage, CancellationToken ct)
    {
        var available = await entitlements.IsEnabledAsync(Capabilities.AiEvalsAdvanced, ct);
        var path = (await org.GetAncestorsAndSelfAsync(unitId, ct)).Reverse().ToList();
        var rows = await LoadAsync(path, ct);
        var own = rows.GetValueOrDefault(unitId);
        var above = path.Where(u => u != unitId && rows.ContainsKey(u)).Select(u => rows[u]).ToList();
        var names = await org.GetNamesAsync(above.Select(a => a.UnitId), ct);
        var tenant = await settings.TenantRulesAsync(ct);
        var mine = own?.Metrics ?? [];
        var keys = mine.Select(m => m.Key).Concat(above.SelectMany(a => a.Metrics.Select(m => m.Key))).Concat(tenant.Keys).Distinct().ToList();
        var rules = ThresholdRules.Effective(keys,
            [(EvalLevels.Platform, ThresholdRules.PlatformRules), (EvalLevels.Tenant, tenant), .. Rules(above), (EvalLevels.Organization, ThresholdRules.Rules(mine))]);
        return new UnitSettingsView(unitId, available, own?.Version ?? 0, mine, own?.Datasets ?? [], await DatasetService.OrgSummariesAsync(session, unitId, ct),
            above.Select(a => new UnitLevelView(a.UnitId, names.GetValueOrDefault(a.UnitId), a.Version, a.Metrics, a.Datasets.Length)).ToList(), rules,
            manage && available, own?.UpdatedAt);
    }

    public async Task<UnitSettingsView> SaveAsync(Guid unitId, UnitSettingsRequest request, CancellationToken ct)
    {
        var (_, manage) = await ReadableAsync(unitId, ct);
        if (!manage)
        {
            throw AppException.Denied();
        }
        await entitlements.DemandAsync(Capabilities.AiEvalsAdvanced, ct);
        var path = (await org.GetAncestorsAndSelfAsync(unitId, ct)).Reverse().ToList();
        var rows = await LoadAsync(path, ct);
        var current = rows.GetValueOrDefault(unitId);
        if (request.Version != (current?.Version ?? 0))
        {
            throw AppException.Conflict("conflict.eval.unit_settings_version");
        }
        var tenant = await settings.TenantRulesAsync(ct);
        var above = path.Where(u => u != unitId && rows.ContainsKey(u)).Select(u => rows[u]);
        var metrics = request.Metrics ?? [];
        var errors = ThresholdRules.Check(metrics, EvalLevels.Organization,
            [(EvalLevels.Platform, ThresholdRules.PlatformRules), (EvalLevels.Tenant, tenant), .. Rules(above)], weightsAllowed: true);
        var datasets = (request.Datasets ?? []).Distinct().ToArray();
        var owned = (await DatasetService.OrgSummariesAsync(session, unitId, ct)).Select(d => d.Id).ToHashSet();
        if (datasets.Length > MaxDatasets || datasets.Any(d => !owned.Contains(d)))
        {
            errors.Add("datasets", "validation.eval.unit_datasets");
        }
        else if (datasets.Length > 0 && metrics.Count == 0)
        {
            errors.Add("metrics", "validation.eval.unit_metrics");
        }
        errors.ThrowIfAny();

        var version = (current?.Version ?? 0) + 1;
        await session.ExecAsync(
            """
            insert into evaluation.unit_settings (tenant_id, org_unit_id, version, metrics, datasets, updated_by, updated_at)
            values (platform.current_tenant_id(), @unitId, @version, @metrics, @datasets, @user, @now)
            on conflict (tenant_id, org_unit_id) do update set version = excluded.version, metrics = excluded.metrics, datasets = excluded.datasets,
              updated_by = excluded.updated_by, updated_at = excluded.updated_at
            """, new { unitId, version, metrics = Jsonb.From(metrics), datasets, user = context.RequireUser(), now = clock.UtcNow }, ct);
        await audit.WriteAsync(new AuditEntry(AuditActions.EvalPolicyChanged, "eval_unit_settings", unitId.ToString(), TargetOrgUnitId: unitId,
            Changes: new { level = EvalLevels.Organization, version, from = current?.Metrics, to = metrics, datasets }), ct);
        return await ViewAsync(unitId, true, ct);
    }
}
