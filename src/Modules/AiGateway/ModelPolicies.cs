using System.Collections.Concurrent;
using System.Text.Json;
using TeamBeats.Audit.Contracts;
using TeamBeats.Authorization.Contracts;
using TeamBeats.Entitlements.Contracts;
using TeamBeats.Messaging;
using TeamBeats.Modules.AiGateway.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Primitives;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.AiGateway;

/// <summary>
/// A tenant model policy (spec 013 FR-006): the allowlist, the default model and fallbacks per use case, the models
/// allowed for Confidential content, inference parameter limits, the region rule and the logging setting.
/// </summary>
public sealed record PolicyDocument(
    List<string> Allowlist,
    Dictionary<string, List<string>> Routes,
    List<string> Confidential,
    int? MaxOutputTokens,
    double? MinTemperature,
    double? MaxTemperature,
    List<string>? Regions,
    string Logging = PolicyDocument.MetadataOnly)
{
    public const string MetadataOnly = "metadata";

    /// <summary>The platform default model set: every model of a default route, nothing for Confidential, no extra limits.</summary>
    public static PolicyDocument Platform(CatalogueSnapshot catalogue) => new(
        [.. catalogue.PlatformRoutes.Values.SelectMany(r => r).Distinct(StringComparer.Ordinal)],
        AiUseCases.All.ToDictionary(u => u, u => catalogue.PlatformRoutes.TryGetValue(u, out var r) ? r.ToList() : [], StringComparer.Ordinal),
        [], null, null, null, null);

    /// <summary>
    /// The models a use case may use, in order: its route, kept to models that are allowed, in the catalogue, not
    /// blocked and able to serve the use case.
    /// </summary>
    public IReadOnlyList<string> RouteOf(string useCase, CatalogueSnapshot catalogue)
    {
        if (!Routes.TryGetValue(useCase, out var route))
        {
            return [];
        }
        var capability = useCase == AiUseCases.Embed ? ModelCapabilities.Embeddings : ModelCapabilities.Chat;
        return [.. route.Where(m => Allowlist.Contains(m) && catalogue.Models.TryGetValue(m, out var e) && e.Active && e.Can(capability))];
    }

    public Dictionary<string, IReadOnlyList<string>> EffectiveRoutes(CatalogueSnapshot catalogue) =>
        AiUseCases.All.ToDictionary(u => u, u => RouteOf(u, catalogue), StringComparer.Ordinal);
}

/// <summary>The policy in force for the tenant and where it came from (<c>tenant</c> or <c>platform</c>).</summary>
public sealed record EffectivePolicy(int Version, string Source, PolicyDocument Document);

public sealed record ModelPolicyRequest(
    List<string>? Allowlist,
    Dictionary<string, List<string>>? Routes,
    List<string>? Confidential,
    int? MaxOutputTokens,
    double? MinTemperature,
    double? MaxTemperature,
    List<string>? Regions,
    string? Logging);

public sealed record RouteChangeView(
    Guid Id,
    string Status,
    int BaseVersion,
    IReadOnlyList<string> UseCases,
    IReadOnlyDictionary<string, IReadOnlyList<string>> Before,
    IReadOnlyDictionary<string, IReadOnlyList<string>> After,
    Guid ProposedBy,
    DateTimeOffset ProposedAt,
    DateTimeOffset? DecidedAt,
    string? OverrideReason,
    IReadOnlyList<string> Failures,
    bool GateAvailable);

public sealed record ModelPolicyView(
    int Version,
    string Source,
    bool Editable,
    string? ReadOnlyReason,
    PolicyDocument Policy,
    IReadOnlyDictionary<string, IReadOnlyList<string>> EffectiveRoutes,
    RouteChangeView? Pending)
{
    public string ETag => ModelPolicies.ETag(Version);
}

public sealed record PolicySaveResult(bool Applied, ModelPolicyView Policy, RouteChangeView? RouteChange);

public sealed record RouteOverrideRequest(string? Reason);

/// <summary>
/// Reads and changes the tenant model policy (spec 013 US1). The gateway reads the policy in force through a short
/// per-tenant cache so a change applies to new calls within 60 seconds (US1 scenario 5). A change to the route behind the
/// assistant or agents is held as a proposed route change for the release gate of spec 034 (FR-019); until that gate is
/// built the AI Policy Administrator applies it with an audited override.
/// </summary>
public sealed class ModelPolicies(
    IDbSession session,
    ITenantContext context,
    IAuthorizationService authz,
    IEntitlementService entitlements,
    ModelCatalogue catalogue,
    IAuditWriter audit,
    IOutbox outbox,
    IClock clock)
{
    public static readonly TimeSpan CacheFor = TimeSpan.FromSeconds(30);
    public const int MaxModels = 50;
    public const int MaxRegions = 10;
    public const int MinReason = 10;
    public const int MaxReason = 1000;

    /// <summary>False until spec 034 publishes gate decisions; the page then says the gate is not available.</summary>
    public const bool GateAvailable = false;

    private static readonly ConcurrentDictionary<Guid, (EffectivePolicy Policy, DateTimeOffset At)> Cache = new();

    internal static void Invalidate(Guid tenant) => Cache.TryRemove(tenant, out _);

    public static string ETag(int version) => $"\"v{version}\"";

    /// <summary>The policy in force for new calls, cached briefly.</summary>
    public async Task<EffectivePolicy> EffectiveAsync(CancellationToken ct)
    {
        var tenant = context.RequireTenant();
        if (Cache.TryGetValue(tenant, out var hit) && clock.UtcNow - hit.At < CacheFor)
        {
            return hit.Policy;
        }
        var policy = await LoadAsync(ct);
        Cache[tenant] = (policy, clock.UtcNow);
        return policy;
    }

    /// <summary>The stored tenant policy when the package allows model choice, else the platform default set (US1 scenario 4).</summary>
    private async Task<EffectivePolicy> LoadAsync(CancellationToken ct)
    {
        var snapshot = await catalogue.SnapshotAsync(ct);
        var stored = await StoredAsync(ct);
        if (stored is { } s && await entitlements.IsEnabledAsync(Capabilities.AiModelChoice, ct))
        {
            return new EffectivePolicy(s.Version, "tenant", s.Document);
        }
        return new EffectivePolicy(stored?.Version ?? 0, "platform", PolicyDocument.Platform(snapshot));
    }

    private async Task<(int Version, PolicyDocument Document)?> StoredAsync(CancellationToken ct, bool forUpdate = false)
    {
        var rows = await session.QueryAsync($"select version, document::text from ai.model_policy{(forUpdate ? " for update" : string.Empty)}", null,
            r => (r.GetInt32(0), JsonSerializer.Deserialize<PolicyDocument>(r.GetString(1), JsonDefaults.Options)!), ct);
        return rows.Count == 0 ? null : rows[0];
    }

    private async Task DemandViewAsync(CancellationToken ct)
    {
        if (!(await authz.CheckAsync(Permissions.AiPolicyManage, AuthzTarget.Tenant, ct)).Allowed &&
            !(await authz.CheckAsync(Permissions.AiPolicyView, AuthzTarget.Tenant, ct)).Allowed)
        {
            throw AppException.Denied();
        }
    }

    public async Task<IReadOnlyList<CatalogueEntry>> CatalogueAsync(CancellationToken ct)
    {
        await DemandViewAsync(ct);
        return [.. (await catalogue.SnapshotAsync(ct)).Models.Values.Where(m => m.Active)];
    }

    public async Task<ModelPolicyView> GetAsync(CancellationToken ct)
    {
        await DemandViewAsync(ct);
        return await ViewAsync(await LoadAsync(ct), ct);
    }

    private async Task<ModelPolicyView> ViewAsync(EffectivePolicy policy, CancellationToken ct)
    {
        var snapshot = await catalogue.SnapshotAsync(ct);
        var manage = (await authz.CheckAsync(Permissions.AiPolicyManage, AuthzTarget.Tenant, ct)).Allowed;
        var choice = await entitlements.IsEnabledAsync(Capabilities.AiModelChoice, ct);
        var reason = !manage ? "ai.policy.read_only_permission" : !choice ? "ai.policy.read_only_package" : null;
        var pending = (await ChangesAsync("where status = 'proposed'", null, ct)).FirstOrDefault();
        return new ModelPolicyView(policy.Version, policy.Source, reason is null, reason, policy.Document, policy.Document.EffectiveRoutes(snapshot), pending);
    }

    public async Task<IReadOnlyList<RouteChangeView>> RouteChangesAsync(CancellationToken ct)
    {
        await DemandViewAsync(ct);
        return await ChangesAsync("order by proposed_at desc limit 50", null, ct);
    }

    private async Task<List<RouteChangeView>> ChangesAsync(string tail, object? args, CancellationToken ct) =>
        await session.QueryAsync(
            $"""
            select id, status, base_version, use_cases, before::text, after::text, proposed_by, proposed_at, decided_at, override_reason, failures::text
            from ai.route_change {tail}
            """, args,
            r => new RouteChangeView(r.GetGuid(0), r.GetString(1), r.GetInt32(2), [.. r.GetFieldValue<string[]>(3)], Routes(r.GetString(4)), Routes(r.GetString(5)),
                r.GetGuid(6), r.GetFieldValue<DateTimeOffset>(7), r.Get<DateTimeOffset?>("decided_at"), r.Get<string>("override_reason"),
                JsonSerializer.Deserialize<List<string>>(r.GetString(10)) ?? [], GateAvailable), ct);

    private static Dictionary<string, IReadOnlyList<string>> Routes(string json) =>
        (JsonSerializer.Deserialize<Dictionary<string, List<string>>>(json) ?? []).ToDictionary(p => p.Key, p => (IReadOnlyList<string>)p.Value, StringComparer.Ordinal);

    /// <summary>
    /// Saves the tenant policy (US1 scenario 5) with <c>If-Match</c>. Changes that leave the assistant and agent routes
    /// as they are apply at once; others are held as a proposed route change (scenario 6).
    /// </summary>
    public async Task<PolicySaveResult> UpdateAsync(string? ifMatch, ModelPolicyRequest request, CancellationToken ct)
    {
        await authz.DemandAsync(Permissions.AiPolicyManage, AuthzTarget.Tenant, ct);
        await entitlements.DemandAsync(Capabilities.AiModelChoice, ct);
        var snapshot = await catalogue.SnapshotAsync(ct);
        var stored = await StoredAsync(ct, forUpdate: true);
        var current = stored is { } s ? new EffectivePolicy(s.Version, "tenant", s.Document) : new EffectivePolicy(0, "platform", PolicyDocument.Platform(snapshot));
        if (ifMatch is not null && ifMatch != ETag(current.Version))
        {
            throw new AppException(ProblemCodes.Conflict, 409, "conflict.ai.policy_changed", null, new Dictionary<string, object?> { ["etag"] = ETag(current.Version) });
        }
        var next = Validate(request, current.Document, snapshot);

        var before = current.Document.EffectiveRoutes(snapshot);
        var after = next.EffectiveRoutes(snapshot);
        var gated = AiUseCases.All.Where(u => AiUseCases.Gated.Contains(u) && !before[u].SequenceEqual(after[u])).ToList();
        await session.ExecAsync("update ai.route_change set status = 'replaced', decided_at = @at where status = 'proposed'", new { at = clock.UtcNow }, ct);
        if (gated.Count == 0)
        {
            var applied = await ApplyAsync(current, next, ct);
            return new PolicySaveResult(true, await ViewAsync(applied, ct), null);
        }

        var me = context.RequireUser();
        var id = Ids.New();
        var beforeGated = gated.ToDictionary(u => u, u => before[u], StringComparer.Ordinal);
        var afterGated = gated.ToDictionary(u => u, u => after[u], StringComparer.Ordinal);
        await session.ExecAsync(
            """
            insert into ai.route_change (tenant_id, id, base_version, document, use_cases, before, after, status, proposed_by, proposed_at)
            values (platform.current_tenant_id(), @id, @base, @document, @useCases, @before, @after, 'proposed', @me, @at)
            """,
            new { id, @base = current.Version, document = Jsonb.From(next), useCases = gated.ToArray(), before = Jsonb.From(beforeGated), after = Jsonb.From(afterGated), me, at = clock.UtcNow },
            ct);
        await audit.WriteAsync(new AuditEntry(AuditActions.AiRouteChangeProposed, "ai_route_change", id.ToString(), Changes: new { useCases = gated, before = beforeGated, after = afterGated }), ct);
        await outbox.EnqueueAsync(new AiModelRouteChangeProposed(id, gated, beforeGated, afterGated), ct);
        var view = await ViewAsync(current, ct);
        return new PolicySaveResult(false, view, view.Pending);
    }

    /// <summary>
    /// Applies a held route change with a reason while the gate is not available or reported blocking failures
    /// (FR-019). Refused when the policy changed since the change was proposed.
    /// </summary>
    public async Task<PolicySaveResult> OverrideAsync(Guid id, RouteOverrideRequest request, CancellationToken ct)
    {
        await authz.DemandAsync(Permissions.AiPolicyManage, AuthzTarget.Tenant, ct);
        await entitlements.DemandAsync(Capabilities.AiModelChoice, ct);
        var reason = request.Reason?.Trim() ?? string.Empty;
        if (reason.Length is < MinReason or > MaxReason)
        {
            throw AppException.Validation("reason", "validation.ai.override_reason");
        }
        var rows = await session.QueryAsync("select status, base_version, document::text from ai.route_change where id = @id for update", new { id },
            r => (Status: r.GetString(0), Base: r.GetInt32(1), Document: JsonSerializer.Deserialize<PolicyDocument>(r.GetString(2), JsonDefaults.Options)!), ct);
        if (rows.Count == 0)
        {
            throw AppException.NotFound();
        }
        var change = rows[0];
        if (change.Status != "proposed")
        {
            throw AppException.Conflict("conflict.ai.route_change_decided");
        }
        var snapshot = await catalogue.SnapshotAsync(ct);
        var stored = await StoredAsync(ct, forUpdate: true);
        var current = stored is { } s ? new EffectivePolicy(s.Version, "tenant", s.Document) : new EffectivePolicy(0, "platform", PolicyDocument.Platform(snapshot));
        if (current.Version != change.Base)
        {
            throw AppException.Conflict("conflict.ai.policy_changed");
        }
        var me = context.RequireUser();
        await session.ExecAsync(
            "update ai.route_change set status = 'overridden', decided_at = @at, decided_by = @me, override_reason = @reason where id = @id",
            new { id, at = clock.UtcNow, me, reason }, ct);
        await audit.WriteAsync(new AuditEntry(AuditActions.AiRouteChangeOverridden, "ai_route_change", id.ToString(), Changes: new { reason }), ct);
        var applied = await ApplyAsync(current, change.Document, ct);
        return new PolicySaveResult(true, await ViewAsync(applied, ct), null);
    }

    private async Task<EffectivePolicy> ApplyAsync(EffectivePolicy current, PolicyDocument next, CancellationToken ct)
    {
        var version = current.Version + 1;
        var me = context.RequireUser();
        await session.ExecAsync(
            """
            insert into ai.model_policy (tenant_id, version, document, updated_by, updated_at)
            values (platform.current_tenant_id(), @version, @document, @me, @at)
            on conflict (tenant_id) do update set version = excluded.version, document = excluded.document, updated_by = excluded.updated_by, updated_at = excluded.updated_at
            """,
            new { version, document = Jsonb.From(next), me, at = clock.UtcNow }, ct);
        await audit.WriteAsync(new AuditEntry(AuditActions.AiPolicyChanged, "ai_policy", context.RequireTenant().ToString(), Changes: new { before = current.Document, after = next }), ct);
        await outbox.EnqueueAsync(new AiPolicyChanged(version), ct);
        var tenant = context.RequireTenant();
        session.OnCommitted(() => Invalidate(tenant));
        return new EffectivePolicy(version, "tenant", next);
    }

    /// <summary>Checks a requested policy against the catalogue and the platform baseline (section 6). Missing fields keep their current value.</summary>
    internal static PolicyDocument Validate(ModelPolicyRequest request, PolicyDocument current, CatalogueSnapshot catalogue)
    {
        var errors = new ValidationErrors();
        var allowlist = (request.Allowlist ?? current.Allowlist).Distinct(StringComparer.Ordinal).ToList();
        if (allowlist.Count > MaxModels)
        {
            errors.Add("allowlist", "validation.ai.too_many");
        }
        foreach (var m in allowlist)
        {
            if (!catalogue.Models.TryGetValue(m, out var e) || !e.Active)
            {
                errors.Add("allowlist", "validation.ai.unknown_model");
                break;
            }
        }

        var routes = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var requested = request.Routes ?? current.Routes;
        foreach (var (useCase, models) in requested)
        {
            if (!AiUseCases.All.Contains(useCase))
            {
                errors.Add($"routes.{useCase}", "validation.ai.use_case");
                continue;
            }
            var capability = useCase == AiUseCases.Embed ? ModelCapabilities.Embeddings : ModelCapabilities.Chat;
            var route = models.Distinct(StringComparer.Ordinal).ToList();
            if (route.Count > ModelCatalogue.MaxRouteLength ||
                route.Any(m => !allowlist.Contains(m) || !catalogue.Models.TryGetValue(m, out var e) || !e.Can(capability)))
            {
                errors.Add($"routes.{useCase}", "validation.ai.route_model");
                continue;
            }
            routes[useCase] = route;
        }
        foreach (var useCase in AiUseCases.All)
        {
            routes.TryAdd(useCase, []);
        }

        var confidential = (request.Confidential ?? current.Confidential).Distinct(StringComparer.Ordinal).ToList();
        if (confidential.Any(m => !allowlist.Contains(m) || !catalogue.Models.TryGetValue(m, out var e) || !e.ConfidentialEligible))
        {
            // FR-007: Confidential only for models the catalogue marks zero retention and no training.
            errors.Add("confidential", "validation.ai.confidential_model");
        }

        var maxTokens = request.MaxOutputTokens ?? current.MaxOutputTokens;
        if (maxTokens is < 1 or > 200_000)
        {
            errors.Add("maxOutputTokens", "validation.ai.out_of_range");
        }
        var minT = request.MinTemperature ?? current.MinTemperature;
        var maxT = request.MaxTemperature ?? current.MaxTemperature;
        if (minT is < 0 or > 2 || maxT is < 0 or > 2 || (minT is { } lo && maxT is { } hi && lo > hi))
        {
            errors.Add("temperature", "validation.ai.temperature");
        }
        var regions = request.Regions ?? current.Regions;
        if (regions is not null && (regions.Count is 0 or > MaxRegions || regions.Any(r => r.Length is < 2 or > 20)))
        {
            errors.Add("regions", "validation.ai.region");
        }
        var logging = request.Logging ?? current.Logging;
        if (logging != PolicyDocument.MetadataOnly)
        {
            // Full text only in time-boxed debug windows (FR-010), never as a standing setting.
            errors.Add("logging", "validation.ai.logging");
        }
        errors.ThrowIfAny();
        return new PolicyDocument(allowlist, routes, confidential, maxTokens, minT, maxT, regions?.Distinct(StringComparer.Ordinal).ToList(), logging);
    }
}
