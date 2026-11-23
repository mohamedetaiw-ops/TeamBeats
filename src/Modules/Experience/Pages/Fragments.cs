using System.Text.Json;
using System.Text.RegularExpressions;
using TeamBeats.Audit.Contracts;
using TeamBeats.Authorization.Contracts;
using TeamBeats.Entitlements.Contracts;
using TeamBeats.Modules.Experience.Components;
using TeamBeats.Modules.Experience.Contracts;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Primitives;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.Experience.Pages;

/// <param name="OrgUnitId">The unit whose pages (and those of the units below it) may place the fragment; null for every page.</param>
/// <param name="Placements">The fragment's content; or leave it out and copy a page region's draft with <paramref name="FromPageId"/> and <paramref name="RegionId"/>.</param>
public sealed record CreateFragmentRequest(string? Key, IReadOnlyDictionary<string, string>? Names, Guid? OrgUnitId,
    IReadOnlyList<Placement>? Placements, Guid? FromPageId = null, string? RegionId = null, string? Note = null);

/// <summary>A new version of a fragment. Pages that place it show the new version at once; earlier versions stay readable.</summary>
public sealed record UpdateFragmentRequest(IReadOnlyDictionary<string, string>? Names, IReadOnlyList<Placement>? Placements,
    Guid? FromPageId = null, string? RegionId = null, string? Note = null);

public sealed record FragmentView(Guid Id, string Key, string Reference, IReadOnlyDictionary<string, string> Names, Guid? OrgUnitId, Guid OwnerId,
    int CurrentVersion, IReadOnlyList<Placement> Placements, DateTimeOffset? ArchivedAt, DateTimeOffset CreatedAt);

public sealed record FragmentVersionView(int Version, string? Note, Guid CreatedBy, DateTimeOffset CreatedAt, IReadOnlyList<Placement> Placements);

public sealed record FragmentImpact(Guid FragmentId, string Key, int PageCount, IReadOnlyList<ImpactPage> Pages);

/// <summary>
/// Governed fragments (spec 032 R2 T027, FR-045): a reusable set of placements with an owner, a scope and immutable versions.
/// A page places one by reference, as a placement whose component is <c>fragment:&lt;key&gt;</c>, never as a copy, so a new
/// version shows on every page that places it. Someone who publishes pages at a scope manages its fragments; editors of
/// pages in that scope (and below it) place them. Fragments need <c>experience.fragments</c>; without it a page's references
/// are blocking findings and render as nothing. A fragment holds components only: no nested fragments and no audience rules,
/// which belong to the reference on the page and apply to the whole fragment.
/// </summary>
public sealed partial class Fragments(
    IDbSession session,
    ITenantContext context,
    IAuthorizationService authz,
    IEntitlementService entitlements,
    IOrganizationHierarchy hierarchy,
    ComponentRegistry registry,
    ComponentAvailability availability,
    IAuditWriter audit,
    IClock clock)
{
    public const string Prefix = "fragment:";
    public const int MaxPlacements = 12;
    /// <summary>Placement ids inside a fragment are short, so the id a page shows (<c>&lt;reference id&gt;-&lt;inner id&gt;</c>) stays within 40 characters.</summary>
    public const int MaxInnerId = 12;
    public const int ListLimit = 50;

    private sealed record Row(Guid Id, string Key, Dictionary<string, string> Names, Guid? OrgUnitId, Guid OwnerId, int CurrentVersion,
        DateTimeOffset? ArchivedAt, DateTimeOffset CreatedAt);

    private const string Select = "select id, key, names::text as names, org_unit_id, owner_id, current_version, archived_at, created_at from experience.fragment";

    private static Row Read(Npgsql.NpgsqlDataReader r) => new(r.Get<Guid>("id"), r.Get<string>("key")!,
        JsonSerializer.Deserialize<Dictionary<string, string>>(r.Get<string>("names")!)!, r.Get<Guid?>("org_unit_id"), r.Get<Guid>("owner_id"),
        r.Get<int>("current_version"), r.Get<DateTimeOffset?>("archived_at"), r.Get<DateTimeOffset>("created_at"));

    private static AuthzTarget Target(Guid? orgUnitId) => orgUnitId is { } u ? AuthzTarget.OrgUnit(u) : AuthzTarget.Tenant;

    public static bool IsReference(string? componentRef) => componentRef?.StartsWith(Prefix, StringComparison.Ordinal) == true;

    /// <summary>Fragments the caller may place or manage: those at scopes where they edit pages, newest first.</summary>
    public async Task<IReadOnlyList<FragmentView>> ListAsync(CancellationToken ct)
    {
        await entitlements.DemandAsync(Capabilities.ExperienceFragments, ct);
        var rows = await session.QueryAsync(Select + " where archived_at is null order by created_at desc", null, Read, ct);
        var shown = new List<FragmentView>();
        foreach (var row in rows)
        {
            if ((await authz.CheckAsync(Permissions.PageEdit, Target(row.OrgUnitId), ct)).Allowed)
            {
                shown.Add(await ViewAsync(row, ct));
            }
        }
        return shown;
    }

    public async Task<FragmentView> GetAsync(Guid id, CancellationToken ct)
    {
        await entitlements.DemandAsync(Capabilities.ExperienceFragments, ct);
        return await ViewAsync(await VisibleAsync(id, Permissions.PageEdit, ct), ct);
    }

    public async Task<FragmentView> CreateAsync(CreateFragmentRequest request, CancellationToken ct)
    {
        await entitlements.DemandAsync(Capabilities.ExperienceFragments, ct);
        if (request.Key is not { } key || !Key().IsMatch(key))
        {
            throw AppException.Validation("key", "validation.fragment.key_invalid");
        }
        if (request.OrgUnitId is { } unit && !await hierarchy.UnitExistsAsync(unit, ct))
        {
            throw AppException.NotFound();
        }
        await authz.DemandVisibleAsync(Permissions.PagePublish, Target(request.OrgUnitId), ct);
        var names = Names(request.Names) ?? throw AppException.Validation("names", "validation.fragment.names_invalid");
        var placements = await ContentAsync(request.Placements, request.FromPageId, request.RegionId, request.OrgUnitId, ct);
        if (await session.ScalarAsync<bool>("select exists (select 1 from experience.fragment where key = @key)", new { key }, ct))
        {
            throw AppException.Conflict("conflict.fragment.key_taken");
        }
        var id = Ids.New();
        var user = context.RequireUser();
        var now = clock.UtcNow;
        await session.ExecAsync(
            """
            insert into experience.fragment (tenant_id, id, key, names, org_unit_id, owner_id, current_version, created_at)
            values (platform.current_tenant_id(), @id, @key, @names, @unit, @user, 1, @now)
            """, new { id, key, names = Jsonb.From(names), unit = request.OrgUnitId, user, now }, ct);
        await InsertVersionAsync(id, 1, placements, request.Note, ct);
        await audit.WriteAsync(new AuditEntry(AuditActions.FragmentChanged, "fragment", id.ToString(), TargetOrgUnitId: request.OrgUnitId,
            Changes: new { key, version = 1, placements = placements.Count }), ct);
        return await ViewAsync((await FindAsync(id, ct))!, ct);
    }

    public async Task<FragmentView> UpdateAsync(Guid id, UpdateFragmentRequest request, CancellationToken ct)
    {
        await entitlements.DemandAsync(Capabilities.ExperienceFragments, ct);
        var row = await VisibleAsync(id, Permissions.PagePublish, ct, forUpdate: true);
        if (row.ArchivedAt is not null)
        {
            throw AppException.Conflict("conflict.fragment.archived");
        }
        var names = request.Names is null ? row.Names : Names(request.Names) ?? throw AppException.Validation("names", "validation.fragment.names_invalid");
        var version = row.CurrentVersion + 1;
        var placements = await ContentAsync(request.Placements, request.FromPageId, request.RegionId, row.OrgUnitId, ct);
        await InsertVersionAsync(id, version, placements, request.Note, ct);
        await session.ExecAsync("update experience.fragment set current_version = @version, names = @names where id = @id",
            new { id, version, names = Jsonb.From(names) }, ct);
        await audit.WriteAsync(new AuditEntry(AuditActions.FragmentChanged, "fragment", id.ToString(), TargetOrgUnitId: row.OrgUnitId,
            Changes: new { key = row.Key, version, placements = placements.Count }), ct);
        return await ViewAsync((await FindAsync(id, ct))!, ct);
    }

    /// <summary>Archives a fragment. Pages that still place it show nothing there and cannot be published until the reference is removed.</summary>
    public async Task ArchiveAsync(Guid id, CancellationToken ct)
    {
        await entitlements.DemandAsync(Capabilities.ExperienceFragments, ct);
        var row = await VisibleAsync(id, Permissions.PagePublish, ct, forUpdate: true);
        if (row.ArchivedAt is not null)
        {
            return;
        }
        await session.ExecAsync("update experience.fragment set archived_at = @now where id = @id", new { id, now = clock.UtcNow }, ct);
        await audit.WriteAsync(new AuditEntry(AuditActions.FragmentArchived, "fragment", id.ToString(), TargetOrgUnitId: row.OrgUnitId,
            Changes: new { key = row.Key }), ct);
    }

    public async Task<IReadOnlyList<FragmentVersionView>> VersionsAsync(Guid id, CancellationToken ct)
    {
        await entitlements.DemandAsync(Capabilities.ExperienceFragments, ct);
        await VisibleAsync(id, Permissions.PageEdit, ct);
        return await session.QueryAsync(
            "select version, note, created_by, created_at, placements::text as p from experience.fragment_version where fragment_id = @id order by version desc",
            new { id }, r => new FragmentVersionView(r.Get<int>("version"), r.Get<string?>("note"), r.Get<Guid>("created_by"),
                r.Get<DateTimeOffset>("created_at"), Parse(r.Get<string>("p")!)), ct);
    }

    /// <summary>The pages whose draft or current release place the fragment, so its manager sees who a new version reaches.</summary>
    public async Task<FragmentImpact> ImpactAsync(Guid id, CancellationToken ct)
    {
        await entitlements.DemandAsync(Capabilities.ExperienceFragments, ct);
        var row = await VisibleAsync(id, Permissions.PageEdit, ct);
        var reference = Prefix + row.Key;
        var pages = await session.QueryAsync(
            """
            select p.id, p.slug, p.org_unit_id from experience.page p
            join experience.page_draft d on d.page_id = p.id
            left join experience.page_release r on r.page_id = p.id and r.release = p.current_release
            where jsonb_path_exists(d.definition, '$.regions[*].placements[*] ? (@.componentRef == $ref)', jsonb_build_object('ref', @reference::text))
               or jsonb_path_exists(coalesce(r.definition, '{}'::jsonb), '$.regions[*].placements[*] ? (@.componentRef == $ref)', jsonb_build_object('ref', @reference::text))
            order by p.slug
            """, new { reference }, r => new ImpactPage(r.Get<Guid>("id"), r.Get<string>("slug")!, r.Get<Guid?>("org_unit_id")), ct);
        return new FragmentImpact(row.Id, row.Key, pages.Count, pages.Take(ListLimit).ToList());
    }

    /// <summary>Fragments a page of <paramref name="orgUnitId"/> may place: tenant-wide ones and those of its unit or a unit above it.</summary>
    internal async Task<IReadOnlyList<FragmentView>> UsableAtAsync(Guid? orgUnitId, CancellationToken ct)
    {
        if (!await entitlements.IsEnabledAsync(Capabilities.ExperienceFragments, ct))
        {
            return [];
        }
        var path = orgUnitId is { } unit ? (await hierarchy.GetAncestorsAndSelfAsync(unit, ct)).ToHashSet() : [];
        var rows = await session.QueryAsync(Select + " where archived_at is null order by key", null, Read, ct);
        var usable = new List<FragmentView>();
        foreach (var row in rows.Where(r => r.OrgUnitId is null || path.Contains(r.OrgUnitId.Value)))
        {
            usable.Add(await ViewAsync(row, ct));
        }
        return usable;
    }

    /// <summary>
    /// The definition with each fragment reference replaced by the fragment's current placements, as a page shows it. Each
    /// placement's id becomes <c>&lt;reference id&gt;-&lt;inner id&gt;</c> and takes the reference's audience rule. A reference
    /// that cannot be shown (unknown or archived fragment, a fragment outside the page's scope, or the package without
    /// fragments) is left out and reported as a blocking finding on the reference.
    /// </summary>
    internal async Task<(PageDefinition Definition, List<Finding> Findings)> ExpandAsync(PageDefinition definition, Guid? orgUnitId, CancellationToken ct)
    {
        var findings = new List<Finding>();
        var keys = definition.Regions.SelectMany(r => r.Placements).Where(p => IsReference(p.ComponentRef))
            .Select(p => p.ComponentRef[Prefix.Length..]).Distinct(StringComparer.Ordinal).ToArray();
        if (keys.Length == 0)
        {
            return (definition, findings);
        }
        var enabled = await entitlements.IsEnabledAsync(Capabilities.ExperienceFragments, ct);
        var current = enabled
            ? (await session.QueryAsync(
                """
                select f.key, f.org_unit_id, v.placements::text as p from experience.fragment f
                join experience.fragment_version v on v.fragment_id = f.id and v.version = f.current_version
                where f.key = any(@keys) and f.archived_at is null
                """, new { keys }, r => (Key: r.Get<string>("key")!, Unit: r.Get<Guid?>("org_unit_id"), Placements: Parse(r.Get<string>("p")!)), ct))
                .ToDictionary(x => x.Key, StringComparer.Ordinal)
            : [];
        var path = orgUnitId is { } unit && current.Values.Any(f => f.Unit is not null) ? (await hierarchy.GetAncestorsAndSelfAsync(unit, ct)).ToHashSet() : [];
        var regions = definition.Regions.Select(region => new PageRegion(region.Id, region.Placements.SelectMany(p =>
        {
            if (!IsReference(p.ComponentRef))
            {
                return [p];
            }
            var key = p.ComponentRef[Prefix.Length..];
            string? reason = !enabled ? "fragment_not_entitled"
                : !current.TryGetValue(key, out var fragment) ? "fragment_unknown"
                : fragment.Unit is { } scope && !path.Contains(scope) ? "fragment_out_of_scope"
                : p.Id is not { Length: > 0 } || p.Id.Length + 1 + fragment.Placements.Max(x => x.Id.Length) > 40 ? "fragment_id_too_long"
                : null;
            if (reason is not null)
            {
                findings.Add(new Finding("validation.page." + reason, Severities.Blocking, region.Id, p.Id));
                return [];
            }
            return current[key].Placements.Select(inner => inner with { Id = $"{p.Id}-{inner.Id}", VisibilityRuleRef = p.VisibilityRuleRef });
        }).ToList())).ToList();
        return (definition with { Regions = regions }, findings);
    }

    private async Task<Row> VisibleAsync(Guid id, string permission, CancellationToken ct, bool forUpdate = false)
    {
        var row = await FindAsync(id, ct, forUpdate) ?? throw AppException.NotFound();
        if (!(await authz.CheckAsync(permission, Target(row.OrgUnitId), ct)).Allowed)
        {
            // An editor who may see the fragment but not manage it is refused; anyone else finds nothing.
            if (permission != Permissions.PageEdit && (await authz.CheckAsync(Permissions.PageEdit, Target(row.OrgUnitId), ct)).Allowed)
            {
                throw AppException.Denied();
            }
            throw AppException.NotFound();
        }
        return row;
    }

    private Task<Row?> FindAsync(Guid id, CancellationToken ct, bool forUpdate = false) =>
        session.QuerySingleOrDefaultAsync(Select + " where id = @id" + (forUpdate ? " for update" : ""), new { id }, Read, ct);

    private async Task<FragmentView> ViewAsync(Row row, CancellationToken ct)
    {
        var json = await session.ScalarAsync<string>(
            "select placements::text from experience.fragment_version where fragment_id = @id and version = @version", new { id = row.Id, version = row.CurrentVersion }, ct);
        return new FragmentView(row.Id, row.Key, Prefix + row.Key, row.Names, row.OrgUnitId, row.OwnerId, row.CurrentVersion, Parse(json!), row.ArchivedAt, row.CreatedAt);
    }

    private async Task InsertVersionAsync(Guid id, int version, IReadOnlyList<Placement> placements, string? note, CancellationToken ct)
    {
        if (note is { Length: > 500 })
        {
            throw AppException.Validation("note", "validation.fragment.note_too_long");
        }
        await session.ExecAsync(
            """
            insert into experience.fragment_version (tenant_id, fragment_id, version, placements, note, created_by, created_at)
            values (platform.current_tenant_id(), @id, @version, @placements, @note, @user, @now)
            """, new { id, version, placements = Jsonb.From(placements), note, user = context.RequireUser(), now = clock.UtcNow }, ct);
    }

    /// <summary>The placements given, or a copy of a page region's draft the caller edits; each is checked at the fragment's scope.</summary>
    private async Task<IReadOnlyList<Placement>> ContentAsync(IReadOnlyList<Placement>? given, Guid? fromPageId, string? regionId, Guid? orgUnitId, CancellationToken ct)
    {
        var placements = given;
        if (placements is null && fromPageId is { } pageId)
        {
            var page = await session.QuerySingleOrDefaultAsync(
                "select p.org_unit_id, d.definition::text as d from experience.page p join experience.page_draft d on d.page_id = p.id where p.id = @pageId",
                new { pageId }, r => (Unit: r.Get<Guid?>("org_unit_id"), Draft: r.Get<string>("d")!), ct);
            if (page.Draft is null || !(await authz.CheckAsync(Permissions.PageEdit, Target(page.Unit), ct)).Allowed)
            {
                throw AppException.NotFound();
            }
            var draft = JsonSerializer.Deserialize<PageDefinition>(page.Draft, JsonDefaults.Options)!;
            placements = draft.Regions.FirstOrDefault(r => r.Id == regionId)?.Placements
                ?? throw AppException.Validation("regionId", "validation.page.region_unknown");
        }
        if (placements is not { Count: > 0 } || placements.Count > MaxPlacements)
        {
            throw AppException.Validation("placements", "validation.fragment.placements_count");
        }
        var available = await availability.ForScopeAsync(orgUnitId, ct);
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var p in placements)
        {
            var reason = p.Id is null || !InnerId().IsMatch(p.Id) || !ids.Add(p.Id) ? "validation.fragment.placement_id_invalid"
                : IsReference(p.ComponentRef) ? "validation.fragment.nested"
                : p.VisibilityRuleRef is not null ? "validation.fragment.audience"
                : registry.Find(p.ComponentRef) is not { } manifest || manifest.State == ComponentStates.Retired ? "validation.page.component_unknown"
                : available(manifest) != Availability.Available ? "validation.page.component_not_permitted"
                : PropertyValidator.Validate(manifest.Properties, p.Props).Count > 0 ? "validation.page.property_invalid"
                : p.Span is { } span && (span < manifest.Placement.MinSpan || span > manifest.Placement.MaxSpan) ? "validation.page.span_invalid"
                : null;
            if (reason is not null)
            {
                throw new AppException(ProblemCodes.ValidationFailed, 422, "validation.fragment.invalid", null,
                    new Dictionary<string, object?> { ["findings"] = new[] { new Finding(reason, Severities.Blocking, null, p.Id) } });
            }
        }
        return placements;
    }

    private static Dictionary<string, string>? Names(IReadOnlyDictionary<string, string>? names)
    {
        if (names is null || names.Count == 0 || names.Any(n => !Primitives.Locales.IsSupported(n.Key) || string.IsNullOrWhiteSpace(n.Value) || n.Value.Length > 120))
        {
            return null;
        }
        return names.ToDictionary(n => n.Key, n => n.Value.Trim(), StringComparer.Ordinal);
    }

    private static List<Placement> Parse(string json) => JsonSerializer.Deserialize<List<Placement>>(json, JsonDefaults.Options)!;

    [GeneratedRegex("^[a-z][a-z0-9-]{2,39}$")]
    private static partial Regex Key();

    [GeneratedRegex("^[a-z0-9][a-z0-9-]{0,11}$")]
    private static partial Regex InnerId();
}
