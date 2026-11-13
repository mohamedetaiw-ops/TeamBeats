using System.Text.Json;
using System.Text.RegularExpressions;
using TeamBeats.Audit.Contracts;
using TeamBeats.Authorization.Contracts;
using TeamBeats.Entitlements.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Primitives;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.Compliance;

/// <summary>A control of a framework: its ID in the framework (5.15, CC6.1, 2-2) and its short title by locale.</summary>
public sealed record FrameworkControl(string Code, IReadOnlyDictionary<string, string> Titles);

/// <summary>A framework of the control library. Platform frameworks ship in code; tenant ones are stored per tenant.</summary>
public sealed record Framework(string Key, string Version, IReadOnlyDictionary<string, string> Names, IReadOnlyList<FrameworkControl> Controls, bool Platform,
    Guid? Id = null, int Revision = 1);

public sealed record FrameworkSummary(string Key, string Version, IReadOnlyDictionary<string, string> Names, int ControlCount, bool Platform, Guid? Id, int Revision);

public sealed record LibraryView(IReadOnlyList<FrameworkSummary> Frameworks, bool CanManage);

public sealed record ControlInput(string? Code, IReadOnlyDictionary<string, string>? Titles);

public sealed record FrameworkRequest(string? Key, string? Version, IReadOnlyDictionary<string, string>? Names, IReadOnlyList<ControlInput>? Controls, int? Revision);

/// <summary>
/// The control library (spec 026 FR-021): ISO 27001:2022 Annex A, the SOC 2 trust services criteria and NCA ECC as
/// versioned platform JSON, plus frameworks the tenant defines. Anyone who reads compliance projects reads the library;
/// tenant frameworks are added and changed with <c>compliance.manage</c> for the whole tenant, since every unit uses them.
/// </summary>
public sealed partial class ComplianceLibrary(IDbSession session, ITenantContext context, IAuthorizationService authz, IEntitlementService entitlements,
    IAuditWriter audit, IClock clock)
{
    public const string TenantPrefix = "tenant.";
    public const int MaxControls = 500;
    public const int MaxTenantFrameworks = 20;

    private static readonly Lazy<IReadOnlyList<Framework>> PlatformSets = new(LoadPlatform);

    public static IReadOnlyList<Framework> Platform => PlatformSets.Value;

    private sealed record StoredFramework(string Key, string Version, Dictionary<string, string> Names, List<StoredControl> Controls);

    private sealed record StoredControl(string Code, Dictionary<string, string> Titles);

    private static IReadOnlyList<Framework> LoadPlatform()
    {
        var assembly = typeof(ComplianceLibrary).Assembly;
        var list = new List<Framework>();
        foreach (var name in assembly.GetManifestResourceNames().Where(n => n.Contains(".Frameworks.", StringComparison.Ordinal)).Order(StringComparer.Ordinal))
        {
            using var stream = assembly.GetManifestResourceStream(name)!;
            var set = JsonSerializer.Deserialize<StoredFramework>(stream, JsonDefaults.Options)!;
            list.Add(new Framework(set.Key, set.Version, set.Names, set.Controls.Select(c => new FrameworkControl(c.Code, c.Titles)).ToList(), true));
        }
        return list;
    }

    [GeneratedRegex("^[a-z0-9][a-z0-9_-]{1,39}$", RegexOptions.CultureInvariant)]
    private static partial Regex Slug();

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9.\-_ ]{0,19}$", RegexOptions.CultureInvariant)]
    private static partial Regex Code();

    internal async Task RequireAsync(CancellationToken ct) => await entitlements.DemandAsync(Capabilities.ComplianceProjects, ct);

    /// <summary>Whether the caller reads or manages compliance anywhere (tenant or any unit).</summary>
    private async Task DemandAnyAsync(CancellationToken ct)
    {
        if (!await authz.HoldsAnywhereAsync(Permissions.ComplianceView, ct) && !await authz.HoldsAnywhereAsync(Permissions.ComplianceManage, ct))
        {
            throw AppException.Denied();
        }
    }

    private async Task<List<Framework>> TenantAsync(CancellationToken ct) =>
        await session.QueryAsync("select id, key, version_label, names::text, controls::text, revision from compliance.framework order by created_at",
            null, r => new Framework(r.GetString(1), r.GetString(2), JsonSerializer.Deserialize<Dictionary<string, string>>(r.GetString(3), JsonDefaults.Options)!,
                JsonSerializer.Deserialize<List<StoredControl>>(r.GetString(4), JsonDefaults.Options)!.Select(c => new FrameworkControl(c.Code, c.Titles)).ToList(),
                false, r.GetGuid(0), r.GetInt32(5)), ct);

    /// <summary>A framework by key, platform or tenant, or null.</summary>
    internal async Task<Framework?> FindAsync(string key, CancellationToken ct) =>
        Platform.FirstOrDefault(f => f.Key == key) ?? (await TenantAsync(ct)).FirstOrDefault(f => f.Key == key);

    public async Task<LibraryView> ListAsync(CancellationToken ct)
    {
        await RequireAsync(ct);
        await DemandAnyAsync(ct);
        var all = Platform.Concat(await TenantAsync(ct));
        return new LibraryView(all.Select(f => new FrameworkSummary(f.Key, f.Version, f.Names, f.Controls.Count, f.Platform, f.Id, f.Revision)).ToList(),
            (await authz.CheckAsync(Permissions.ComplianceManage, AuthzTarget.Tenant, ct)).Allowed);
    }

    public async Task<Framework> GetAsync(string key, CancellationToken ct)
    {
        await RequireAsync(ct);
        await DemandAnyAsync(ct);
        return await FindAsync(key, ct) ?? throw AppException.NotFound();
    }

    private static (string Version, Dictionary<string, string> Names, List<StoredControl> Controls) Validate(FrameworkRequest request, Framework? current)
    {
        var errors = new ValidationErrors();
        var version = request.Version?.Trim() ?? current?.Version ?? "";
        if (version.Length is < 1 or > 40)
        {
            errors.Add("version", "validation.compliance.framework_version");
        }
        var names = (request.Names ?? current?.Names ?? new Dictionary<string, string>())
            .Where(n => !string.IsNullOrWhiteSpace(n.Value)).ToDictionary(n => n.Key, n => n.Value.Trim(), StringComparer.Ordinal);
        if (names.Count == 0 || names.Values.Any(n => n.Length > 120))
        {
            errors.Add("names", "validation.compliance.framework_name");
        }
        var controls = request.Controls is { } input
            ? input.Select(c => new StoredControl(c.Code?.Trim() ?? "",
                (c.Titles ?? new Dictionary<string, string>()).Where(t => !string.IsNullOrWhiteSpace(t.Value)).ToDictionary(t => t.Key, t => t.Value.Trim(), StringComparer.Ordinal))).ToList()
            : current?.Controls.Select(c => new StoredControl(c.Code, c.Titles.ToDictionary(t => t.Key, t => t.Value))).ToList() ?? [];
        if (controls.Count is 0 or > MaxControls)
        {
            errors.Add("controls", "validation.compliance.framework_controls");
        }
        else if (controls.Any(c => !Code().IsMatch(c.Code) || c.Titles.Count == 0 || c.Titles.Values.Any(t => t.Length > 200)))
        {
            errors.Add("controls", "validation.compliance.control_invalid");
        }
        else if (controls.Select(c => c.Code.ToUpperInvariant()).Distinct().Count() != controls.Count)
        {
            errors.Add("controls", "validation.compliance.control_code_taken");
        }
        errors.ThrowIfAny();
        return (version, names, controls);
    }

    /// <summary>Adds a tenant framework. Its key is "tenant." and the slug given, and cannot change later.</summary>
    public async Task<Framework> CreateAsync(FrameworkRequest request, CancellationToken ct)
    {
        await RequireAsync(ct);
        await authz.DemandAsync(Permissions.ComplianceManage, AuthzTarget.Tenant, ct);
        var slug = request.Key?.Trim().ToLowerInvariant() ?? "";
        if (!Slug().IsMatch(slug))
        {
            throw AppException.Validation("key", "validation.compliance.framework_key");
        }
        var (version, names, controls) = Validate(request, null);
        var existing = await TenantAsync(ct);
        if (existing.Count >= MaxTenantFrameworks)
        {
            throw AppException.Validation("key", "validation.compliance.framework_limit");
        }
        var key = TenantPrefix + slug;
        if (existing.Any(f => f.Key == key))
        {
            throw AppException.Validation("key", "validation.compliance.framework_key_taken");
        }
        var id = Ids.New();
        var now = clock.UtcNow;
        await session.ExecAsync(
            """
            insert into compliance.framework (tenant_id, id, key, names, version_label, controls, created_by, created_at, updated_at)
            values (@t, @id, @key, @names, @version, @controls, @by, @now, @now)
            """, new { t = context.RequireTenant(), id, key, names = Jsonb.From(names), version, controls = Jsonb.From(controls), by = context.RequireUser(), now }, ct);
        await audit.WriteAsync(new AuditEntry(AuditActions.ComplianceFrameworkChanged, "compliance_framework", id.ToString(),
            Changes: new { key, version, controls = controls.Count, created = true }), ct);
        return new Framework(key, version, names, controls.Select(c => new FrameworkControl(c.Code, c.Titles)).ToList(), false, id);
    }

    /// <summary>
    /// Changes a tenant framework. Projects already started keep the controls they copied; new projects get the new list.
    /// <paramref name="request"/>'s revision must be the one read, so two editors do not overwrite each other.
    /// </summary>
    public async Task<Framework> UpdateAsync(Guid id, FrameworkRequest request, CancellationToken ct)
    {
        await RequireAsync(ct);
        await authz.DemandAsync(Permissions.ComplianceManage, AuthzTarget.Tenant, ct);
        var current = (await TenantAsync(ct)).FirstOrDefault(f => f.Id == id) ?? throw AppException.NotFound();
        if (request.Revision is { } revision && revision != current.Revision)
        {
            throw AppException.Conflict("conflict.compliance.changed");
        }
        var (version, names, controls) = Validate(request, current);
        await session.ExecAsync(
            """
            update compliance.framework set names = @names, version_label = @version, controls = @controls, revision = revision + 1, updated_at = @now
            where id = @id
            """, new { id, names = Jsonb.From(names), version, controls = Jsonb.From(controls), now = clock.UtcNow }, ct);
        await audit.WriteAsync(new AuditEntry(AuditActions.ComplianceFrameworkChanged, "compliance_framework", id.ToString(),
            Changes: new { key = current.Key, from = current.Version, to = version, controls = controls.Count }), ct);
        return new Framework(current.Key, version, names, controls.Select(c => new FrameworkControl(c.Code, c.Titles)).ToList(), false, id, current.Revision + 1);
    }
}
