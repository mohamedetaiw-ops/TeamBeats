using System.Diagnostics.Metrics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using TeamBeats.Audit.Contracts;
using TeamBeats.Authorization.Contracts;
using TeamBeats.Entitlements.Contracts;
using TeamBeats.Messaging;
using TeamBeats.Modules.Experience.Contracts;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Primitives;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.Experience.Themes;

public sealed record BrandRequest(string? Name, IReadOnlyDictionary<string, TokenValue>? Tokens, IReadOnlyDictionary<string, string>? Assets,
    bool StrictContrast = false);

public sealed record BrandSummary(Guid Id, string Name, int CurrentVersion, bool Seeded, IReadOnlyList<string> AssignedScopes);

public sealed record BrandView(Guid Id, string Name, int Version, bool Seeded, bool StrictContrast, IReadOnlyDictionary<string, TokenValue> Tokens,
    IReadOnlyDictionary<string, string> Assets, IReadOnlyDictionary<string, string> AssetUrls, IReadOnlyList<ContrastFinding> Findings);

/// <summary>One token of the schema with its platform defaults, for editors (spec 033 T011).</summary>
public sealed record TokenSchemaEntry(string Name, string Group, string Type, string CssName, string Light, string? Dark, string? Compact,
    string? Script, IReadOnlyList<string> Modes);

public sealed record AssignmentRequest(Guid? BrandId, IReadOnlyDictionary<string, TokenValue>? Overrides);

public sealed record AssignmentView(string Scope, Guid? BrandId, IReadOnlyDictionary<string, TokenValue> Overrides, IReadOnlyList<ContrastFinding> Findings);

public sealed record LockRequest(IReadOnlyList<string>? Settings);

public sealed record LockView(string Setting, string Scope, bool Inherited, bool Permanent);

public sealed record AssetView(string Reference, string Url, string ContentType, int Bytes);

/// <summary>Check answer: findings plus the CSS custom properties of the edited brand per mode, for the live preview.</summary>
public sealed record BrandCheck(IReadOnlyList<ContrastFinding> Findings, IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> Preview);

public sealed record ThemeVariant(string Mode, string Density, string CssUrl, IReadOnlyDictionary<string, string> Tokens);

public sealed record EffectiveTheme(string Scope, Guid BrandId, int BrandVersion, IReadOnlyDictionary<string, string> Assets,
    IReadOnlyList<ThemeVariant> Variants, IReadOnlyList<string> Locks, IReadOnlyDictionary<string, TokenSource>? Provenance);

/// <summary>
/// Brand profiles, assignments, locks and the effective theme (spec 033 R1). Brands are a tenant library edited with
/// <c>theme.edit</c> at tenant scope; applying a brand or overriding tokens at a scope needs <c>theme.publish</c> there, and
/// locks need <c>config_lock.manage</c>. Org scopes need <c>experience.org_branding</c>. A scope outside the caller's grants,
/// or of another tenant, answers not found. Theme data never feeds authorization (FR-017).
/// </summary>
public sealed partial class ThemeService(
    IDbSession session,
    ITenantContext context,
    IAuthorizationService authz,
    IEntitlementService entitlements,
    IOrganizationHierarchy hierarchy,
    ITenantSettingsReader tenantSettings,
    IAuditWriter audit,
    IOutbox outbox,
    TenantObjectStorage storage,
    IMemoryCache cache,
    ILogger<ThemeService> logger)
{
    public const string TenantScope = "tenant";
    private const string NoScope = "00000000-0000-0000-0000-000000000000";

    private static readonly Meter Meter = new("TeamBeats.Experience");
    private static readonly Counter<long> InvalidTokens = Meter.CreateCounter<long>("theme.invalid_token");

    private static readonly IReadOnlyList<string> AssetSlots = ["logo", "lockup", "favicon", "default_image", "sign_in_image"];

    private sealed record BrandRow(Guid Id, string Name, int Version, bool Seeded, bool Strict, Dictionary<string, TokenValue> Tokens,
        Dictionary<string, string> Assets);

    private sealed record AssignmentRow(Guid? Scope, Guid? BrandId, Dictionary<string, TokenValue> Overrides);

    private sealed record LockRow(Guid? Scope, string Setting);

    // Brands

    public async Task<IReadOnlyList<BrandSummary>> ListBrandsAsync(CancellationToken ct)
    {
        await DemandBrandReaderAsync(ct);
        await EnsureSeededAsync(ct);
        var assignments = await AssignmentsAsync(ct);
        return await session.QueryAsync("select id, name, current_version, seeded from experience.brand order by created_at, id", null,
            r => new BrandSummary(r.GetGuid(0), r.GetString(1), r.GetInt32(2), r.GetBoolean(3),
                assignments.Where(a => a.BrandId == r.GetGuid(0)).Select(a => ScopeName(a.Scope)).ToList()), ct);
    }

    public async Task<BrandView> GetBrandAsync(Guid id, CancellationToken ct)
    {
        await DemandBrandReaderAsync(ct);
        await EnsureSeededAsync(ct);
        var brand = await BrandAsync(id, null, ct) ?? throw AppException.NotFound();
        return View(brand, ThemeEngine.Check(ThemeEngine.Resolve([Layer(0, null, brand)], new Dictionary<string, (int, Guid?)>()), brand.Strict));
    }

    public async Task<BrandView> CreateBrandAsync(BrandRequest request, CancellationToken ct)
    {
        await authz.DemandAsync(Permissions.ThemeEdit, AuthzTarget.Tenant, ct);
        await EnsureSeededAsync(ct);
        var (tokens, assets) = await ValidateBrandAsync(request, ct);
        var findings = BrandFindings(tokens, request.StrictContrast);

        await session.ExecAsync("select pg_advisory_xact_lock(hashtext('experience.brand:' || @t::text))", new { t = context.RequireTenant() }, ct);
        var count = await session.ScalarAsync<long>("select count(*) from experience.brand", null, ct);
        var limit = await entitlements.GetLimitAsync(Capabilities.ExperienceMaxBrands, ct);
        if (limit is { } max && count >= max)
        {
            throw new AppException(ProblemCodes.EntitlementQuotaExhausted, 403, "validation.theme.brand_limit", null,
                new Dictionary<string, object?> { ["capability"] = Capabilities.ExperienceMaxBrands, ["limit"] = max });
        }

        var id = Ids.New();
        await session.ExecAsync("insert into experience.brand (tenant_id, id, name, current_version) values (@t, @id, @name, 1)",
            new { t = context.RequireTenant(), id, name = request.Name!.Trim() }, ct);
        await InsertVersionAsync(id, 1, tokens, assets, request.StrictContrast, context.ActorId, ct);
        await PublishedAsync(id, 1, null, ct);
        return View((await BrandAsync(id, null, ct))!, findings);
    }

    /// <summary>R1 publishes on save: every update is a new immutable version (drafts and approval are R2).</summary>
    public async Task<BrandView> UpdateBrandAsync(Guid id, BrandRequest request, CancellationToken ct)
    {
        await authz.DemandAsync(Permissions.ThemeEdit, AuthzTarget.Tenant, ct);
        var current = await session.QuerySingleOrDefaultAsync("select current_version from experience.brand where id = @id for update",
            new { id }, r => (int?)r.GetInt32(0), ct) ?? throw AppException.NotFound();
        var assignments = await AssignmentsAsync(ct);
        if (assignments.Any(a => a.BrandId == id))
        {
            // A brand in use changes what employees see, so the change is a publish.
            await authz.DemandAsync(Permissions.ThemePublish, AuthzTarget.Tenant, ct);
        }
        var (tokens, assets) = await ValidateBrandAsync(request, ct);
        var findings = BrandFindings(tokens, request.StrictContrast);
        foreach (var scope in assignments.Where(a => a.BrandId == id).Select(a => a.Scope))
        {
            ThrowIfBlocking(ThemeEngine.Check(await ResolveAsync(scope, ct, brandOverride: (id, tokens)), request.StrictContrast), "tokens");
        }

        var version = current + 1;
        await InsertVersionAsync(id, version, tokens, assets, request.StrictContrast, context.ActorId, ct);
        await session.ExecAsync("update experience.brand set name = @name, current_version = @v where id = @id",
            new { id, v = version, name = request.Name!.Trim() }, ct);
        await PublishedAsync(id, version, current, ct);
        return View((await BrandAsync(id, null, ct))!, findings);
    }

    /// <summary>Findings for a candidate version of a brand without saving (plan API, <c>/check</c>).</summary>
    public async Task<BrandCheck> CheckBrandAsync(Guid id, BrandRequest request, CancellationToken ct)
    {
        await authz.DemandAsync(Permissions.ThemeEdit, AuthzTarget.Tenant, ct);
        if (await BrandAsync(id, null, ct) is null)
        {
            throw AppException.NotFound();
        }
        var errors = new ValidationErrors();
        TokenSchema.Validate(request.Tokens, errors);
        errors.ThrowIfAny();
        var tokens = new Dictionary<string, TokenValue>(request.Tokens ?? new Dictionary<string, TokenValue>());
        ThrowIfCycles(tokens);
        var resolved = ThemeEngine.Resolve([new ThemeLayer(0, null, "brand", null, null, tokens)], new Dictionary<string, (int, Guid?)>());
        var preview = ThemeModes.Modes.ToDictionary(m => m, m => ThemeEngine.Properties(resolved.Variants[$"{m}.{ThemeModes.Comfortable}"]));
        return new BrandCheck(ThemeEngine.Check(resolved, request.StrictContrast), preview);
    }

    // Brand assets (US4)

    /// <summary>
    /// Stores a brand image after the spec 001 checks (PNG, JPEG, static SVG, 1 MB) under the tenant's storage prefix.
    /// The asset takes effect only when a brand version refers to it, which is the audited publish.
    /// </summary>
    public async Task<AssetView> UploadAssetAsync(string? contentType, byte[] data, CancellationToken ct)
    {
        await authz.DemandAsync(Permissions.ThemeEdit, AuthzTarget.Tenant, ct);
        var extension = ImageValidator.Validate("asset", contentType, data);
        var mime = extension switch { "png" => "image/png", "jpg" => "image/jpeg", _ => "image/svg+xml" };
        var id = Ids.New();
        var key = $"experience/assets/{id:N}.{extension}";
        using (var stream = new MemoryStream(data))
        {
            await storage.PutAsync(key, stream, mime, ct);
        }
        await session.ExecAsync(
            "insert into experience.brand_asset (tenant_id, id, object_key, content_type, bytes, created_by) values (@t, @id, @key, @mime, @bytes, @a)",
            new { t = context.RequireTenant(), id, key, mime, bytes = data.Length, a = context.ActorId }, ct);
        await audit.WriteAsync(new AuditEntry(AuditActions.BrandAssetUploaded, "brand_asset", id.ToString(),
            Changes: new { contentType = mime, bytes = data.Length }), ct);
        return new AssetView($"asset:{id}", AssetUrl(id), mime, data.Length);
    }

    /// <summary>Any member of the tenant may read brand assets, because the shell shows them. Other tenants get not found.</summary>
    public async Task<(Stream Content, string ContentType)> OpenAssetAsync(Guid id, CancellationToken ct)
    {
        var row = await session.QuerySingleOrDefaultAsync(
            "select object_key, content_type from experience.brand_asset where id = @id", new { id },
            r => (Key: r.Get<string>("object_key")!, Type: r.Get<string>("content_type")!), ct);
        if (row.Key is null)
        {
            throw AppException.NotFound();
        }
        var stream = await storage.OpenReadAsync(row.Key, ct) ?? throw AppException.NotFound();
        return (stream, row.Type);
    }

    public static string AssetUrl(Guid id) => $"/api/v1/experience/assets/{id}";

    // Assignments and overrides

    public async Task<AssignmentView> GetAssignmentAsync(string scope, CancellationToken ct)
    {
        var unit = await ScopeAsync(scope, Permissions.ThemeEdit, ct);
        await EnsureSeededAsync(ct);
        var row = (await AssignmentsAsync(ct)).FirstOrDefault(a => a.Scope == unit);
        return new AssignmentView(ScopeName(unit), row?.BrandId, row?.Overrides ?? [], []);
    }

    public async Task<AssignmentView> SetAssignmentAsync(string scope, AssignmentRequest request, CancellationToken ct)
    {
        var unit = await ScopeAsync(scope, Permissions.ThemePublish, ct);
        await EnsureSeededAsync(ct);
        var overrides = new Dictionary<string, TokenValue>(request.Overrides ?? new Dictionary<string, TokenValue>());
        var errors = new ValidationErrors();
        TokenSchema.Validate(overrides, errors, "overrides");
        if (unit is null && request.BrandId is null)
        {
            errors.Add("brandId", "validation.theme.brand_required");
        }
        errors.ThrowIfAny();
        if (request.BrandId is { } brandId && await BrandAsync(brandId, null, ct) is null)
        {
            throw AppException.Validation("brandId", "validation.theme.brand_unknown");
        }

        // A value locked by a parent scope cannot be overridden here (FR-019). The scope that locked it may still set it.
        var locks = await LocksForAsync(unit, ct);
        var locked = overrides.Keys.Where(k => locks.TryGetValue(k, out var l) && l.Scope != unit).ToList();
        if (locked.Count > 0)
        {
            throw AppException.Validation(locked.ToDictionary(k => "overrides." + k, _ => new[] { "validation.theme.token_locked" }));
        }
        ThrowIfCycles(overrides);

        var previous = (await AssignmentsAsync(ct)).FirstOrDefault(a => a.Scope == unit);
        var strict = request.BrandId is { } b ? (await BrandAsync(b, null, ct))!.Strict : false;
        var findings = ThemeEngine.Check(await ResolveAsync(unit, ct, assignmentOverride: new AssignmentRow(unit, request.BrandId, overrides)), strict);
        ThrowIfBlocking(findings, "overrides");

        var actor = context.ActorId;
        if (unit is not null && request.BrandId is null && overrides.Count == 0)
        {
            await session.ExecAsync("delete from experience.brand_assignment where scope_org_unit_id = @u", new { u = unit }, ct);
            if (previous is not null)
            {
                await audit.WriteAsync(new AuditEntry(AuditActions.BrandUnassigned, "brand_assignment", ScopeName(unit), TargetOrgUnitId: unit,
                    Changes: new { before = new { brand = previous.BrandId, overrides = previous.Overrides.Keys.Order() } }), ct);
            }
        }
        else
        {
            await session.ExecAsync(
                $"""
                insert into experience.brand_assignment (tenant_id, scope_org_unit_id, brand_id, overrides, changed_by)
                values (@t, @u, @b, @o, @a)
                on conflict (tenant_id, coalesce(scope_org_unit_id, '{NoScope}'::uuid))
                do update set brand_id = excluded.brand_id, overrides = excluded.overrides, changed_by = excluded.changed_by, changed_at = now()
                """,
                new { t = context.RequireTenant(), u = unit, b = request.BrandId, o = Jsonb.From(overrides), a = actor }, ct);
            await audit.WriteAsync(new AuditEntry(AuditActions.BrandAssigned, "brand_assignment", ScopeName(unit), TargetOrgUnitId: unit,
                Changes: new
                {
                    before = previous is null ? null : new { brand = previous.BrandId, overrides = previous.Overrides.Keys.Order() },
                    after = new { brand = request.BrandId, overrides = overrides.Keys.Order() },
                }), ct);
        }
        await outbox.EnqueueAsync(new BrandAssignmentChanged(unit, request.BrandId), ct);
        await BumpAsync(ct);
        return new AssignmentView(ScopeName(unit), request.BrandId, overrides, findings);
    }

    // Locks

    public async Task<IReadOnlyList<LockView>> GetLocksAsync(string scope, CancellationToken ct)
    {
        var unit = await ScopeAsync(scope, Permissions.ConfigLockManage, ct, alternative: Permissions.ThemeEdit);
        await EnsureSeededAsync(ct);
        return await LockViewsAsync(unit, ct);
    }

    /// <summary>Replaces the locks set at this scope. Locks of parent scopes are shown as inherited and cannot be removed here.</summary>
    public async Task<IReadOnlyList<LockView>> SetLocksAsync(string scope, LockRequest request, CancellationToken ct)
    {
        var unit = await ScopeAsync(scope, Permissions.ConfigLockManage, ct);
        await EnsureSeededAsync(ct);
        var wanted = (request.Settings ?? []).Distinct(StringComparer.Ordinal).ToList();
        var errors = new ValidationErrors();
        if (wanted.Count > TokenSchema.All.Count + TokenSchema.ShellSettings.Count)
        {
            errors.Add("settings", "validation.theme.too_many_locks");
        }
        foreach (var s in wanted.Where(s => !TokenSchema.IsLockable(s)))
        {
            errors.Add("settings." + s, "validation.theme.lock_unknown");
        }
        if (unit is null)
        {
            foreach (var s in TokenSchema.PermanentLocks.Where(p => !wanted.Contains(p)))
            {
                errors.Add("settings." + s, "validation.theme.lock_required");
            }
        }
        errors.ThrowIfAny();

        var before = (await session.QueryAsync("select setting from experience.experience_lock where scope_org_unit_id is not distinct from @u",
            new { u = unit }, r => r.GetString(0), ct)).Order(StringComparer.Ordinal).ToList();
        var after = wanted.Order(StringComparer.Ordinal).ToList();
        if (!before.SequenceEqual(after))
        {
            await session.ExecAsync("delete from experience.experience_lock where scope_org_unit_id is not distinct from @u and not (setting = any(@keep))",
                new { u = unit, keep = after.ToArray() }, ct);
            foreach (var s in after.Except(before))
            {
                await session.ExecAsync(
                    "insert into experience.experience_lock (tenant_id, scope_org_unit_id, setting, locked_by) values (@t, @u, @s, @a)",
                    new { t = context.RequireTenant(), u = unit, s, a = context.ActorId }, ct);
            }
            await audit.WriteAsync(new AuditEntry(AuditActions.ExperienceLockChanged, "experience_lock", ScopeName(unit), TargetOrgUnitId: unit,
                Changes: new { before, after }), ct);
            await outbox.EnqueueAsync(new ExperienceLockChanged(unit, after), ct);
            await BumpAsync(ct);
        }
        return await LockViewsAsync(unit, ct);
    }

    // Effective theme

    /// <summary>
    /// The effective theme for a scope (any tenant member may read it, since pages shared across units keep their owner's
    /// brand). Provenance is included only for <c>config_history.view</c> at that scope (FR-018).
    /// </summary>
    public async Task<EffectiveTheme> ThemeAsync(string? scope, CancellationToken ct)
    {
        var unit = await ReadableScopeAsync(scope, ct);
        var (resolved, brand, assets, locks) = await CachedAsync(unit, ct);
        var name = ScopeName(unit);
        var variants = ThemeEngine.VariantKeys().Select(v =>
        {
            var values = resolved.Variants[v.Key];
            return new ThemeVariant(v.Mode, v.Density,
                $"/api/v1/experience/theme.css?scope={name}&mode={v.Mode}&density={v.Density}&v={Hash(ThemeEngine.Css(values))}", values);
        }).ToList();
        var history = (await authz.CheckAsync(Permissions.ConfigHistoryView, unit is { } u ? AuthzTarget.OrgUnit(u) : AuthzTarget.Tenant, ct)).Allowed;
        return new EffectiveTheme(name, brand.Id, brand.Version, assets, variants, locks, history ? resolved.Provenance : null);
    }

    public async Task<(string Css, string Hash)> CssAsync(string? scope, string? mode, string? density, CancellationToken ct)
    {
        var m = mode ?? ThemeModes.Light;
        var d = density ?? ThemeModes.Comfortable;
        if (!ThemeModes.Modes.Contains(m) || !ThemeModes.Densities.Contains(d))
        {
            throw AppException.NotFound();
        }
        var unit = await ReadableScopeAsync(scope, ct);
        var (resolved, _, _, _) = await CachedAsync(unit, ct);
        var css = ThemeEngine.Css(resolved.Variants[$"{m}.{d}"]);
        return (css, Hash(css));
    }

    // Internals

    private async Task<(ResolvedTheme Theme, BrandRow Brand, IReadOnlyDictionary<string, string> Assets, IReadOnlyList<string> Locks)> CachedAsync(
        Guid? unit, CancellationToken ct)
    {
        await EnsureSeededAsync(ct);
        var version = await session.ScalarAsync<long?>("select version from experience.theme_state", null, ct) ?? 0;
        var hierarchyVersion = await hierarchy.GetVersionAsync(ct);
        var key = ("theme", context.RequireTenant(), unit, version, hierarchyVersion);
        if (cache.TryGetValue(key, out (ResolvedTheme, BrandRow, IReadOnlyDictionary<string, string>, IReadOnlyList<string>) hit))
        {
            return hit;
        }
        var (layers, brand) = await LayersAsync(unit, ct, null, null);
        var locks = await LocksForAsync(unit, ct);
        var resolved = ThemeEngine.Resolve(layers, locks.ToDictionary(l => l.Key, l => (l.Value.Depth, l.Value.Scope)));
        foreach (var token in resolved.InvalidTokens)
        {
            InvalidTokens.Add(1, new KeyValuePair<string, object?>("token", token));
            logger.LogWarning("theme.invalid_token {Token} at scope {Scope}", token, ScopeName(unit));
        }
        var entry = (resolved, brand, (IReadOnlyDictionary<string, string>)brand.Assets, (IReadOnlyList<string>)locks.Keys.Order(StringComparer.Ordinal).ToList());
        cache.Set(key, entry, TimeSpan.FromMinutes(10));
        return entry;
    }

    private async Task<ResolvedTheme> ResolveAsync(Guid? unit, CancellationToken ct, AssignmentRow? assignmentOverride = null,
        (Guid Id, Dictionary<string, TokenValue> Tokens)? brandOverride = null)
    {
        var (layers, _) = await LayersAsync(unit, ct, assignmentOverride, brandOverride);
        var locks = await LocksForAsync(unit, ct);
        return ThemeEngine.Resolve(layers, locks.ToDictionary(l => l.Key, l => (l.Value.Depth, l.Value.Scope)));
    }

    /// <summary>Platform, then the tenant brand and overrides, then each unit from the root down (spec 033 resolver order).</summary>
    private async Task<(List<ThemeLayer> Layers, BrandRow Brand)> LayersAsync(Guid? unit, CancellationToken ct, AssignmentRow? assignmentOverride,
        (Guid Id, Dictionary<string, TokenValue> Tokens)? brandOverride)
    {
        var chain = await ChainAsync(unit, ct);
        var assignments = (await AssignmentsAsync(ct)).Where(a => a.Scope != assignmentOverride?.Scope || assignmentOverride is null).ToList();
        if (assignmentOverride is not null)
        {
            assignments.Add(assignmentOverride);
        }
        var layers = new List<ThemeLayer>();
        BrandRow? effective = null;
        for (var depth = 0; depth < chain.Count; depth++)
        {
            var scope = chain[depth];
            var assignment = assignments.FirstOrDefault(a => a.Scope == scope);
            if (assignment is null)
            {
                continue;
            }
            if (assignment.BrandId is { } brandId && await BrandAsync(brandId, null, ct) is { } brand)
            {
                if (brandOverride is { } o && o.Id == brandId)
                {
                    brand = brand with { Tokens = o.Tokens };
                }
                layers.Add(Layer(depth, scope, brand));
                effective = brand;
            }
            if (assignment.Overrides.Count > 0)
            {
                layers.Add(new ThemeLayer(depth, scope, "override", null, null, assignment.Overrides));
            }
        }
        return (layers, effective ?? throw new InvalidOperationException("The tenant has no brand assigned."));
    }

    /// <summary>Tenant (null) first, then org units from the root to the unit.</summary>
    private async Task<List<Guid?>> ChainAsync(Guid? unit, CancellationToken ct)
    {
        var chain = new List<Guid?> { null };
        if (unit is { } u)
        {
            chain.AddRange((await hierarchy.GetAncestorsAndSelfAsync(u, ct)).Reverse().Select(id => (Guid?)id));
        }
        return chain;
    }

    /// <summary>Locks that apply at a scope: its own and every ancestor's, with the shallowest lock winning.</summary>
    private async Task<Dictionary<string, (int Depth, Guid? Scope)>> LocksForAsync(Guid? unit, CancellationToken ct)
    {
        var chain = await ChainAsync(unit, ct);
        var rows = await session.QueryAsync("select scope_org_unit_id, setting from experience.experience_lock", null,
            r => new LockRow(r.Get<Guid?>("scope_org_unit_id"), r.GetString(1)), ct);
        var result = new Dictionary<string, (int, Guid?)>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            var depth = chain.IndexOf(row.Scope);
            if (depth >= 0 && (!result.TryGetValue(row.Setting, out var existing) || depth < existing.Item1))
            {
                result[row.Setting] = (depth, row.Scope);
            }
        }
        return result;
    }

    private async Task<IReadOnlyList<LockView>> LockViewsAsync(Guid? unit, CancellationToken ct) =>
        (await LocksForAsync(unit, ct))
            .Select(l => new LockView(l.Key, ScopeName(l.Value.Scope), l.Value.Scope != unit, l.Value.Scope is null && TokenSchema.PermanentLocks.Contains(l.Key)))
            .OrderBy(l => l.Setting, StringComparer.Ordinal).ToList();

    private async Task<List<AssignmentRow>> AssignmentsAsync(CancellationToken ct) =>
        await session.QueryAsync("select scope_org_unit_id, brand_id, overrides::text as o from experience.brand_assignment", null,
            r => new AssignmentRow(r.Get<Guid?>("scope_org_unit_id"), r.Get<Guid?>("brand_id"),
                JsonSerializer.Deserialize<Dictionary<string, TokenValue>>(r.Get<string>("o")!, JsonDefaults.Options)!), ct);

    private async Task<BrandRow?> BrandAsync(Guid id, int? version, CancellationToken ct) =>
        await session.QuerySingleOrDefaultAsync(
            """
            select b.id, b.name, v.version, b.seeded, v.strict_contrast, v.tokens::text as tokens, v.assets::text as assets
            from experience.brand b join experience.brand_version v on v.brand_id = b.id and v.version = coalesce(@v, b.current_version)
            where b.id = @id
            """, new { id, v = version },
            r => new BrandRow(r.GetGuid(0), r.GetString(1), r.GetInt32(2), r.GetBoolean(3), r.GetBoolean(4),
                JsonSerializer.Deserialize<Dictionary<string, TokenValue>>(r.Get<string>("tokens")!, JsonDefaults.Options)!,
                JsonSerializer.Deserialize<Dictionary<string, string>>(r.Get<string>("assets")!, JsonDefaults.Options)!), ct);

    private static ThemeLayer Layer(int depth, Guid? scope, BrandRow brand) => new(depth, scope, "brand", brand.Id, brand.Version, brand.Tokens);

    private async Task<(Dictionary<string, TokenValue> Tokens, Dictionary<string, string> Assets)> ValidateBrandAsync(BrandRequest request,
        CancellationToken ct)
    {
        var errors = new ValidationErrors();
        if (string.IsNullOrWhiteSpace(request.Name) || request.Name.Trim().Length > 80)
        {
            errors.Add("name", "validation.theme.brand_name");
        }
        TokenSchema.Validate(request.Tokens, errors);
        foreach (var (slot, reference) in request.Assets ?? new Dictionary<string, string>())
        {
            if (!AssetSlots.Contains(slot))
            {
                errors.Add("assets." + slot, "validation.theme.asset_slot_unknown");
            }
            else if (!AssetReference().IsMatch(reference ?? ""))
            {
                errors.Add("assets." + slot, "validation.theme.asset_reference_invalid");
            }
            else if (reference!.StartsWith("asset:", StringComparison.Ordinal)
                && await session.ScalarAsync<long>("select count(*) from experience.brand_asset where id = @id",
                    new { id = Guid.Parse(reference["asset:".Length..]) }, ct) == 0)
            {
                // Uploaded assets only, and only this tenant's: row-level security hides the rest.
                errors.Add("assets." + slot, "validation.theme.asset_reference_invalid");
            }
        }
        errors.ThrowIfAny();
        var tokens = new Dictionary<string, TokenValue>(request.Tokens ?? new Dictionary<string, TokenValue>());
        ThrowIfCycles(tokens);
        return (tokens, new Dictionary<string, string>(request.Assets ?? new Dictionary<string, string>()));
    }

    private static IReadOnlyList<ContrastFinding> BrandFindings(Dictionary<string, TokenValue> tokens, bool strict)
    {
        var findings = ThemeEngine.Check(ThemeEngine.Resolve([new ThemeLayer(0, null, "brand", null, null, tokens)],
            new Dictionary<string, (int, Guid?)>()), strict);
        ThrowIfBlocking(findings, "tokens");
        return findings;
    }

    private static void ThrowIfCycles(Dictionary<string, TokenValue> tokens)
    {
        var cycles = ThemeEngine.AliasCycles([new ThemeLayer(0, null, "brand", null, null, tokens)]);
        if (cycles.Count > 0)
        {
            throw AppException.Validation(cycles.ToDictionary(c => "tokens." + c, _ => new[] { "validation.theme.alias_cycle" }));
        }
    }

    private static void ThrowIfBlocking(IReadOnlyList<ContrastFinding> findings, string field)
    {
        if (findings.Any(f => f.Severity == ThemeEngine.Blocking))
        {
            throw new AppException(ProblemCodes.ValidationFailed, 422, "validation.theme.invalid", null,
                new Dictionary<string, object?> { ["findings"] = findings, ["field"] = field });
        }
    }

    private async Task InsertVersionAsync(Guid id, int version, Dictionary<string, TokenValue> tokens, Dictionary<string, string> assets, bool strict,
        Guid? actor, CancellationToken ct) =>
        await session.ExecAsync(
            """
            insert into experience.brand_version (tenant_id, brand_id, version, tokens, assets, strict_contrast, created_by)
            values (@t, @id, @v, @tokens, @assets, @strict, @a)
            """,
            new { t = context.RequireTenant(), id, v = version, tokens = Jsonb.From(tokens), assets = Jsonb.From(assets), strict, a = actor }, ct);

    private async Task PublishedAsync(Guid id, int version, int? previous, CancellationToken ct)
    {
        await audit.WriteAsync(new AuditEntry(AuditActions.ThemePublished, "brand", id.ToString(),
            Changes: new { brand = id, version, previousVersion = previous, scope = TenantScope }), ct);
        await outbox.EnqueueAsync(new ThemePublished(id, version), ct);
        await BumpAsync(ct);
    }

    private async Task BumpAsync(CancellationToken ct) =>
        await session.ExecAsync(
            "insert into experience.theme_state (tenant_id, version) values (@t, 1) on conflict (tenant_id) do update set version = experience.theme_state.version + 1",
            new { t = context.RequireTenant() }, ct);

    /// <summary>
    /// The spec 001 logo and primary color become version 1 of a default brand at tenant scope, with the default lock set,
    /// the first time the feature runs for a tenant (spec 033 section 6). It joins the request's transaction, which every theme
    /// endpoint commits, and runs once: later calls find the seeded brand, and a concurrent first call waits on the unique index.
    /// </summary>
    private async Task EnsureSeededAsync(CancellationToken ct)
    {
        if (await session.ScalarAsync<bool>("select exists (select 1 from experience.brand where seeded)", null, ct))
        {
            return;
        }
        var branding = await tenantSettings.GetBrandingAsync(ct);
        var tokens = new Dictionary<string, TokenValue>();
        if (branding.PrimaryColor is { } color && TokenSchema.Check(TokenSchema.ByName["color.accent.base"], color) is null)
        {
            // The light value comes from the tenant color; dark keeps the platform value, so dark mode stays readable.
            tokens["color.accent.base"] = new TokenValue(color, new Dictionary<string, string> { [ThemeModes.Dark] = TokenSchema.ByName["color.accent.base"].Dark! });
        }
        var assets = new Dictionary<string, string>();
        if (branding.HasLogo)
        {
            assets["logo"] = "tenant:logo";
        }
        var tenant = context.RequireTenant();
        var id = await session.ScalarAsync<Guid?>(
            """
            insert into experience.brand (tenant_id, id, name, current_version, seeded) values (@t, @id, 'Default', 1, true)
            on conflict (tenant_id) where seeded do nothing returning id
            """, new { t = tenant, id = Ids.New() }, ct);
        if (id is not { } brandId)
        {
            return;
        }
        await InsertVersionAsync(brandId, 1, tokens, assets, false, null, ct);
        await session.ExecAsync(
            $"""
            insert into experience.brand_assignment (tenant_id, scope_org_unit_id, brand_id) values (@t, null, @b)
            on conflict (tenant_id, coalesce(scope_org_unit_id, '{NoScope}'::uuid)) do nothing
            """, new { t = tenant, b = brandId }, ct);
        foreach (var setting in TokenSchema.DefaultLocks)
        {
            await session.ExecAsync(
                $"""
                insert into experience.experience_lock (tenant_id, scope_org_unit_id, setting) values (@t, null, @s)
                on conflict (tenant_id, coalesce(scope_org_unit_id, '{NoScope}'::uuid), setting) do nothing
                """, new { t = tenant, s = setting }, ct);
        }
        await BumpAsync(ct);
    }

    private async Task DemandBrandReaderAsync(CancellationToken ct)
    {
        if (!await authz.HoldsAnywhereAsync(Permissions.ThemeEdit, ct) && !await authz.HoldsAnywhereAsync(Permissions.ThemePublish, ct))
        {
            throw AppException.Denied();
        }
    }

    /// <summary>
    /// Resolves a scope for an admin operation. An org scope needs <c>experience.org_branding</c>; a scope the caller holds no
    /// grant for, or of another tenant, answers not found so neighbouring scopes are not revealed (US4 scenario 3).
    /// </summary>
    private async Task<Guid?> ScopeAsync(string scope, string permission, CancellationToken ct, string? alternative = null)
    {
        Guid? unit = scope == TenantScope ? null : Guid.TryParse(scope, out var g) ? g : throw AppException.NotFound();
        if (unit is { } u)
        {
            await entitlements.DemandAsync(Capabilities.ExperienceOrgBranding, ct);
            if (!await hierarchy.UnitExistsAsync(u, ct))
            {
                throw AppException.NotFound();
            }
        }
        var target = unit is { } id ? AuthzTarget.OrgUnit(id) : AuthzTarget.Tenant;
        if ((await authz.CheckAsync(permission, target, ct)).Allowed
            || (alternative is not null && (await authz.CheckAsync(alternative, target, ct)).Allowed))
        {
            return unit;
        }
        if (unit is null)
        {
            throw AppException.Denied();
        }
        throw AppException.NotFound();
    }

    private async Task<Guid?> ReadableScopeAsync(string? scope, CancellationToken ct)
    {
        if (scope is null or TenantScope)
        {
            return null;
        }
        if (!Guid.TryParse(scope, out var unit) || !await hierarchy.UnitExistsAsync(unit, ct))
        {
            throw AppException.NotFound();
        }
        return unit;
    }

    private static string ScopeName(Guid? unit) => unit?.ToString() ?? TenantScope;

    private static BrandView View(BrandRow brand, IReadOnlyList<ContrastFinding> findings) =>
        new(brand.Id, brand.Name, brand.Version, brand.Seeded, brand.Strict, brand.Tokens, brand.Assets,
            brand.Assets.ToDictionary(a => a.Key, a => AssetUrlOf(a.Value)), findings);

    /// <summary>Where the browser loads an asset reference: the spec 001 logo, or an uploaded brand asset.</summary>
    public static string AssetUrlOf(string reference) =>
        reference == "tenant:logo" ? "/api/v1/tenant/branding/logo" : AssetUrl(Guid.Parse(reference["asset:".Length..]));

    public static IReadOnlyList<string> Slots => AssetSlots;

    public static string Hash(string css) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(css)))[..16];

    [GeneratedRegex("^(tenant:logo|asset:[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12})$")]
    private static partial Regex AssetReference();
}
