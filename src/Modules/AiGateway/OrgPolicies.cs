using System.Collections.Concurrent;
using System.Text.Json;
using TeamBeats.Audit.Contracts;
using TeamBeats.Authorization.Contracts;
using TeamBeats.Modules.AiGateway.Contracts;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Primitives;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.AiGateway;

/// <summary>
/// What an org unit narrows (spec 013 FR-009). A field left empty follows the policy above. <paramref name="MaxClassification"/>
/// is the highest classification calls of the unit may carry.
/// </summary>
public sealed record OrgPolicyDocument(List<string>? Allowlist, List<string>? Confidential, string? MaxClassification, int? MaxOutputTokens, double? MinTemperature,
    double? MaxTemperature);

public sealed record OrgPolicyRequest(List<string>? Allowlist, List<string>? Confidential, string? MaxClassification, int? MaxOutputTokens, double? MinTemperature,
    double? MaxTemperature);

/// <summary>The rules in force for calls of a unit, after the tenant policy and every unit policy from the root down.</summary>
public sealed record UnitLimits(List<string> Allowlist, List<string> Confidential, string MaxClassification, int? MaxOutputTokens, double? MinTemperature,
    double? MaxTemperature);

/// <summary>
/// A unit's own restrictions with the limits it inherits (<paramref name="Parent"/>), so the page shows what may still
/// be narrowed. <paramref name="Source"/> names, per field, where the parent rule comes from: <c>tenant</c> or a unit ID.
/// </summary>
public sealed record OrgPolicyView(Guid UnitId, int Version, OrgPolicyDocument? Policy, UnitLimits Parent, IReadOnlyDictionary<string, string> Source, bool Editable)
{
    public string ETag => ModelPolicies.ETag(Version);
}

public sealed record OrgPolicySummary(Guid UnitId, int Version, OrgPolicyDocument Policy, DateTimeOffset UpdatedAt);

/// <summary>
/// Org unit model policies (spec 013 US7, FR-009). An AI Policy Administrator of the unit, or of the tenant, narrows the
/// models, the Confidential models, the highest classification, the output tokens and the temperature range for calls
/// whose acting unit is the unit or one of its children. A save that would loosen any rule above is refused, naming the
/// rule and where it comes from. The gateway reads every unit policy of the tenant through a short cache.
/// </summary>
public sealed class OrgPolicies(IDbSession session, ITenantContext context, IAuthorizationService authz, IOrganizationHierarchy hierarchy, ModelPolicies policies,
    IAuditWriter audit, IClock clock)
{
    public static readonly TimeSpan CacheFor = TimeSpan.FromSeconds(30);
    private static readonly ConcurrentDictionary<Guid, (IReadOnlyDictionary<Guid, OrgPolicyDocument> Policies, DateTimeOffset At)> Cache = new();

    internal static void Invalidate(Guid tenant) => Cache.TryRemove(tenant, out _);

    private static readonly string[] Levels = ["public", "internal", "confidential", "restricted"];

    private async Task<IReadOnlyDictionary<Guid, OrgPolicyDocument>> AllAsync(CancellationToken ct)
    {
        var tenant = context.RequireTenant();
        if (Cache.TryGetValue(tenant, out var hit) && clock.UtcNow - hit.At < CacheFor)
        {
            return hit.Policies;
        }
        var rows = await session.QueryAsync("select unit_id, document::text from ai.org_policy", null,
            r => (r.GetGuid(0), JsonSerializer.Deserialize<OrgPolicyDocument>(r.GetString(1), JsonDefaults.Options)!), ct);
        var all = rows.ToDictionary(r => r.Item1, r => r.Item2);
        Cache[tenant] = (all, clock.UtcNow);
        return all;
    }

    private static UnitLimits Start(PolicyDocument tenant) =>
        new([.. tenant.Allowlist], [.. tenant.Confidential], "restricted", tenant.MaxOutputTokens, tenant.MinTemperature, tenant.MaxTemperature);

    private static int Rank(string level) => Array.IndexOf(Levels, level);

    private static UnitLimits Narrow(UnitLimits limits, OrgPolicyDocument p) => new(
        p.Allowlist is { } a ? [.. limits.Allowlist.Where(a.Contains)] : limits.Allowlist,
        p.Confidential is { } c ? [.. limits.Confidential.Where(c.Contains)] : limits.Confidential,
        p.MaxClassification is { } m && Rank(m) >= 0 && Rank(m) < Rank(limits.MaxClassification) ? m : limits.MaxClassification,
        p.MaxOutputTokens is { } tokens ? Math.Min(tokens, limits.MaxOutputTokens ?? int.MaxValue) : limits.MaxOutputTokens,
        p.MinTemperature is { } lo ? Math.Max(lo, limits.MinTemperature ?? 0) : limits.MinTemperature,
        p.MaxTemperature is { } hi ? Math.Min(hi, limits.MaxTemperature ?? 2) : limits.MaxTemperature);

    /// <summary>
    /// The limits for a call whose acting unit is <paramref name="unit"/>, also narrowed by every unit in
    /// <paramref name="others"/> (US7 scenario 3: the strictest policy of the units the request is about). Null when no
    /// unit policy applies, so the tenant policy is used as it is.
    /// </summary>
    internal async Task<UnitLimits?> LimitsAsync(PolicyDocument tenant, Guid? unit, IReadOnlyCollection<Guid>? others, CancellationToken ct)
    {
        var units = new List<Guid>();
        if (unit is { } u)
        {
            units.Add(u);
        }
        units.AddRange(others ?? []);
        if (units.Count == 0)
        {
            return null;
        }
        var all = await AllAsync(ct);
        if (all.Count == 0)
        {
            return null;
        }
        var paths = await hierarchy.GetPathsAsync(units.Distinct(), ct);
        var limits = Start(tenant);
        var any = false;
        foreach (var id in paths.Values.SelectMany(p => p).Distinct())
        {
            if (all.TryGetValue(id, out var p))
            {
                limits = Narrow(limits, p);
                any = true;
            }
        }
        return any ? limits : null;
    }

    /// <summary>Applies unit limits to the tenant policy for one call: the allowlist and Confidential models shrink and the parameter limits narrow.</summary>
    internal static PolicyDocument Apply(PolicyDocument tenant, UnitLimits limits) => tenant with
    {
        Allowlist = limits.Allowlist,
        Confidential = limits.Confidential,
        MaxOutputTokens = limits.MaxOutputTokens,
        MinTemperature = limits.MinTemperature,
        MaxTemperature = limits.MaxTemperature,
    };

    internal static DataClassification Ceiling(UnitLimits limits) => Classifications.Parse(limits.MaxClassification) ?? DataClassification.Restricted;

    private async Task<bool> MayAsync(string permission, Guid unit, CancellationToken ct) => (await authz.CheckAsync(permission, AuthzTarget.OrgUnit(unit), ct)).Allowed;

    private async Task DemandViewAsync(Guid unit, CancellationToken ct)
    {
        if (!await MayAsync(Permissions.AiPolicyManage, unit, ct) && !await MayAsync(Permissions.AiPolicyView, unit, ct))
        {
            throw AppException.NotFound();
        }
    }

    /// <summary>The unit policies the caller may see: all of them for a tenant-wide viewer, else those of units in their scope.</summary>
    public async Task<IReadOnlyList<OrgPolicySummary>> ListAsync(CancellationToken ct)
    {
        if (!await authz.HoldsAnywhereAsync(Permissions.AiPolicyManage, ct) && !await authz.HoldsAnywhereAsync(Permissions.AiPolicyView, ct))
        {
            throw AppException.Denied();
        }
        var rows = await session.QueryAsync("select unit_id, version, document::text, updated_at from ai.org_policy order by updated_at desc", null,
            r => new OrgPolicySummary(r.GetGuid(0), r.GetInt32(1), JsonSerializer.Deserialize<OrgPolicyDocument>(r.GetString(2), JsonDefaults.Options)!,
                r.GetFieldValue<DateTimeOffset>(3)), ct);
        var visible = new List<OrgPolicySummary>();
        foreach (var row in rows)
        {
            if (await MayAsync(Permissions.AiPolicyManage, row.UnitId, ct) || await MayAsync(Permissions.AiPolicyView, row.UnitId, ct))
            {
                visible.Add(row);
            }
        }
        return visible;
    }

    private async Task<(int Version, OrgPolicyDocument Document)?> StoredAsync(Guid unit, CancellationToken ct, bool forUpdate = false)
    {
        var rows = await session.QueryAsync($"select version, document::text from ai.org_policy where unit_id = @unit{(forUpdate ? " for update" : string.Empty)}", new { unit },
            r => (r.GetInt32(0), JsonSerializer.Deserialize<OrgPolicyDocument>(r.GetString(1), JsonDefaults.Options)!), ct);
        return rows.Count == 0 ? null : rows[0];
    }

    /// <summary>The limits a unit inherits, field by field with where each comes from: the tenant policy, then each unit above it from the root down.</summary>
    private async Task<(UnitLimits Limits, Dictionary<string, string> Source, List<(string Source, OrgPolicyDocument? Unit)> Levels)> ParentAsync(Guid unit, CancellationToken ct)
    {
        var tenant = (await policies.EffectiveAsync(ct)).Document;
        var all = await AllAsync(ct);
        var path = (await hierarchy.GetPathsAsync([unit], ct)).GetValueOrDefault(unit) ?? [];
        var limits = Start(tenant);
        var source = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["allowlist"] = "tenant", ["confidential"] = "tenant", ["maxClassification"] = "tenant", ["maxOutputTokens"] = "tenant", ["temperature"] = "tenant",
        };
        var levels = new List<(string, OrgPolicyDocument?)> { ("tenant", null) };
        foreach (var id in path.Where(id => id != unit))
        {
            if (!all.TryGetValue(id, out var p))
            {
                continue;
            }
            levels.Add((id.ToString(), p));
            limits = Narrow(limits, p);
            if (p.Allowlist is not null) source["allowlist"] = id.ToString();
            if (p.Confidential is not null) source["confidential"] = id.ToString();
            if (p.MaxClassification is not null) source["maxClassification"] = id.ToString();
            if (p.MaxOutputTokens is not null) source["maxOutputTokens"] = id.ToString();
            if (p.MinTemperature is not null || p.MaxTemperature is not null) source["temperature"] = id.ToString();
        }
        return (limits, source, levels);
    }

    public async Task<OrgPolicyView> GetAsync(Guid unit, CancellationToken ct)
    {
        if (!await hierarchy.UnitExistsAsync(unit, ct))
        {
            throw AppException.NotFound();
        }
        await DemandViewAsync(unit, ct);
        var stored = await StoredAsync(unit, ct);
        var (limits, source, _) = await ParentAsync(unit, ct);
        return new OrgPolicyView(unit, stored?.Version ?? 0, stored?.Document, limits, source, await MayAsync(Permissions.AiPolicyManage, unit, ct));
    }

    /// <summary>Saves a unit's restrictions as the next version, only when the version sent is current and nothing loosens a rule above.</summary>
    public async Task<OrgPolicyView> SaveAsync(Guid unit, string? ifMatch, OrgPolicyRequest request, CancellationToken ct)
    {
        if (!await hierarchy.UnitExistsAsync(unit, ct))
        {
            throw AppException.NotFound();
        }
        await DemandViewAsync(unit, ct);
        await authz.DemandAsync(Permissions.AiPolicyManage, AuthzTarget.OrgUnit(unit), ct);
        var stored = await StoredAsync(unit, ct, forUpdate: true);
        var version = stored?.Version ?? 0;
        if (ifMatch is not null && ifMatch != ModelPolicies.ETag(version))
        {
            throw new AppException(ProblemCodes.Conflict, 409, "conflict.ai.org_policy_changed", null, new Dictionary<string, object?> { ["etag"] = ModelPolicies.ETag(version) });
        }
        var (limits, source, _) = await ParentAsync(unit, ct);
        var next = Validate(request, limits, source);
        var me = context.RequireUser();
        await session.ExecAsync(
            """
            insert into ai.org_policy (tenant_id, unit_id, version, document, updated_by, updated_at)
            values (platform.current_tenant_id(), @unit, @version, @document, @me, @at)
            on conflict (tenant_id, unit_id) do update set version = excluded.version, document = excluded.document, updated_by = excluded.updated_by,
              updated_at = excluded.updated_at
            """, new { unit, version = version + 1, document = Jsonb.From(next), me, at = clock.UtcNow }, ct);
        await audit.WriteAsync(new AuditEntry(AuditActions.AiOrgPolicyChanged, "ai_org_policy", unit.ToString(), TargetOrgUnitId: unit,
            Changes: new { before = stored?.Document, after = next }), ct);
        var tenant = context.RequireTenant();
        session.OnCommitted(() => Invalidate(tenant));
        return new OrgPolicyView(unit, version + 1, next, limits, source, true);
    }

    /// <summary>Removes a unit's restrictions, so it follows the policy above again.</summary>
    public async Task RemoveAsync(Guid unit, CancellationToken ct)
    {
        await DemandViewAsync(unit, ct);
        await authz.DemandAsync(Permissions.AiPolicyManage, AuthzTarget.OrgUnit(unit), ct);
        var stored = await StoredAsync(unit, ct, forUpdate: true) ?? throw AppException.NotFound();
        await session.ExecAsync("delete from ai.org_policy where unit_id = @unit", new { unit }, ct);
        await audit.WriteAsync(new AuditEntry(AuditActions.AiOrgPolicyChanged, "ai_org_policy", unit.ToString(), TargetOrgUnitId: unit,
            Changes: new { before = stored.Document, after = (OrgPolicyDocument?)null }), ct);
        var tenant = context.RequireTenant();
        session.OnCommitted(() => Invalidate(tenant));
    }

    /// <summary>
    /// Checks a unit's restrictions against what it inherits (US7 scenario 2). A model the parent does not allow, a
    /// Confidential model the parent does not allow for Confidential, a higher classification, more output tokens or a
    /// wider temperature range is refused with the field and where the parent rule comes from.
    /// </summary>
    internal static OrgPolicyDocument Validate(OrgPolicyRequest request, UnitLimits parent, IReadOnlyDictionary<string, string> source)
    {
        var errors = new ValidationErrors();
        var named = new Dictionary<string, object?>();
        void Loosens(string field)
        {
            errors.Add(field, "validation.ai.org_policy_loosens");
            named[field] = source[field == "minTemperature" || field == "maxTemperature" ? "temperature" : field];
        }

        var allowlist = request.Allowlist?.Distinct(StringComparer.Ordinal).ToList();
        if (allowlist is not null && (allowlist.Count > ModelPolicies.MaxModels || allowlist.Any(m => !parent.Allowlist.Contains(m))))
        {
            Loosens("allowlist");
        }
        var confidential = request.Confidential?.Distinct(StringComparer.Ordinal).ToList();
        if (confidential is not null && confidential.Any(m => !parent.Confidential.Contains(m) || (allowlist is not null && !allowlist.Contains(m))))
        {
            Loosens("confidential");
        }
        if (request.MaxClassification is { } level)
        {
            if (Rank(level) < 0)
            {
                errors.Add("maxClassification", "validation.ai.classification");
            }
            else if (Rank(level) > Rank(parent.MaxClassification))
            {
                Loosens("maxClassification");
            }
        }
        if (request.MaxOutputTokens is { } tokens && (tokens < 1 || tokens > (parent.MaxOutputTokens ?? 200_000)))
        {
            Loosens("maxOutputTokens");
        }
        if (request.MinTemperature is { } lo && (lo < (parent.MinTemperature ?? 0) || lo > 2))
        {
            Loosens("minTemperature");
        }
        if (request.MaxTemperature is { } hi && (hi > (parent.MaxTemperature ?? 2) || hi < 0))
        {
            Loosens("maxTemperature");
        }
        if (request.MinTemperature is { } a && request.MaxTemperature is { } b && a > b)
        {
            errors.Add("temperature", "validation.ai.temperature");
        }
        if (errors.Any)
        {
            throw new AppException(ProblemCodes.ValidationFailed, 400, null, errors.Entries.GroupBy(e => e.Field).ToDictionary(g => g.Key, g => g.Select(e => e.Key).ToArray()),
                new Dictionary<string, object?> { ["parentRules"] = named });
        }
        return new OrgPolicyDocument(allowlist, confidential, request.MaxClassification, request.MaxOutputTokens, request.MinTemperature, request.MaxTemperature);
    }
}
