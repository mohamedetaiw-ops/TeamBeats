using System.Text.Json;
using Npgsql;
using TeamBeats.Authorization.Contracts;
using TeamBeats.Entitlements.Contracts;
using TeamBeats.Modules.Services.Contracts;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Primitives;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.Services;

public sealed record CategoryView(Guid Id, IReadOnlyDictionary<string, string> Names, int Position);

/// <summary>
/// A catalogue card. Linked apps carry their URL and whether sign-in is single sign-on (FR-002). <paramref name="Icon"/>
/// is the service's own icon, null for its category's (FR-018). For a form service the card also says who approves
/// (<see cref="ServiceApprovers"/>), the working days the approval step allows, and the viewer's latest request of it
/// (FR-019); these are null on the detail page and for linked and module services.
/// </summary>
public sealed record ServiceCard(
    Guid Id,
    string Kind,
    IReadOnlyDictionary<string, string> Names,
    IReadOnlyDictionary<string, string> Descriptions,
    Guid? CategoryId,
    string? Url,
    bool Sso,
    string? LaunchRoute,
    bool Favourite,
    string? Icon = null,
    string? Approver = null,
    int? DueWorkingDays = null,
    LatestRequest? Latest = null);

/// <summary>The viewer's most recent request of a service, filed by them or for them.</summary>
public sealed record LatestRequest(Guid Id, string Number, string Status);

/// <summary>Who approves a form service, as the card names it (FR-019).</summary>
public static class ServiceApprovers
{
    public const string None = "none";
    public const string LineManager = "line_manager";
    public const string Role = "role";
    public const string Named = "named";

    public static string Of(ServiceRoute route) => route.Approval?.Approvers.Kind switch
    {
        null => None,
        ApproverKinds.LineManager => LineManager,
        ApproverKinds.PermissionAtRequesterUnit => Role,
        _ => Named,
    };
}

public sealed record CatalogueView(IReadOnlyList<CategoryView> Categories, IReadOnlyList<ServiceCard> Services, IReadOnlyList<Guid> Recent);

/// <summary>A service a person may use, with the published form they fill in.</summary>
public sealed record ServiceDetail(ServiceCard Service, int? Version, FormDefinition Form, bool AllowOnBehalf, int MaxAttachments, bool HasApproval, string Classification);

/// <summary>What the catalogue stores for one service; also the shape of the owner's view.</summary>
public sealed record ServiceRow(
    Guid Id,
    string Kind,
    string? ModuleKey,
    IReadOnlyDictionary<string, string> Names,
    IReadOnlyDictionary<string, string> Descriptions,
    IReadOnlyDictionary<string, IReadOnlyList<string>> Synonyms,
    Guid? CategoryId,
    ServiceAudience Audience,
    Guid? OwnerOrgUnitId,
    string Classification,
    string? Url,
    bool Sso,
    string? LaunchRoute,
    string Status,
    int? CurrentVersion,
    DateTimeOffset UpdatedAt,
    string? Icon = null);

/// <summary>The facts an audience is checked against (FR-001): units with their parents, groups and membership type.</summary>
public sealed record AudienceFacts(IReadOnlySet<Guid> Units, IReadOnlySet<Guid> Groups, string? MembershipType)
{
    public bool Matches(ServiceAudience a) =>
        (a.OrgUnits is not { Count: > 0 } || a.OrgUnits.Any(Units.Contains))
        && (a.Groups is not { Count: > 0 } || a.Groups.Any(Groups.Contains))
        && (a.MembershipTypes is not { Count: > 0 } || (MembershipType is { } t && a.MembershipTypes.Contains(t)));
}

/// <summary>
/// The service catalogue of spec 016 US1: published services whose audience includes the viewer, by category, with
/// search, favourites and recent use. Module services (FR-010) join the catalogue from their registrations.
/// </summary>
public sealed class Catalogue(
    IDbSession session,
    ITenantContext context,
    IOrganizationHierarchy hierarchy,
    ITenantMemberships memberships,
    IAuthorizationProvisioning groups,
    IEntitlementService entitlements,
    IEnumerable<IModuleServiceSource> moduleSources,
    IClock clock,
    IAuthorizationService authz)
{
    public const int RecentCount = 8;
    private AudienceFacts? viewer;
    private AppReach? appReach;

    /// <summary>
    /// What an application app calling the public API sees of the catalogue (spec 035 section 7): nothing without a request
    /// role, and with one the services offered to someone in the units the role covers. Groups and membership types say
    /// who may use a service, so they are checked for the person a request is filed for, never for the app.
    /// </summary>
    private sealed record AppReach(OrgCoverage Coverage, IReadOnlySet<Guid> UnitsAndAncestors)
    {
        public bool Offers(ServiceAudience a) =>
            Coverage.Any && (Coverage.WholeTenant || a.OrgUnits is not { Count: > 0 } || a.OrgUnits.Any(UnitsAndAncestors.Contains));
    }

    private async Task<AppReach> AppReachAsync(CancellationToken ct)
    {
        if (appReach is not null)
        {
            return appReach;
        }
        var coverage = await authz.CoverageAsync(Permissions.ServiceRequestRead, ct);
        var units = new HashSet<Guid>();
        if (!coverage.WholeTenant)
        {
            foreach (var unit in coverage.Units)
            {
                units.UnionWith(await hierarchy.GetAncestorsAndSelfAsync(unit, ct));
            }
        }
        return appReach = new AppReach(coverage, units);
    }

    /// <summary>Whether the caller, a person or an application app, is offered a service with this audience.</summary>
    private async Task<bool> OffersAsync(ServiceAudience audience, CancellationToken ct) =>
        context.ActorType == ActorType.App ? (await AppReachAsync(ct)).Offers(audience) : (await ViewerAsync(ct)).Matches(audience);

    internal const string Columns =
        """
        id, kind, module_key, names::text, descriptions::text, synonyms::text, category_id, audience::text, owner_org_unit_id, classification,
        url, sso, launch_route, status, current_version, updated_at, icon
        """;

    internal static ServiceRow Read(NpgsqlDataReader r) => new(
        r.GetGuid(0), r.GetString(1), r.Get<string?>("module_key"),
        Json<Dictionary<string, string>>(r.GetString(3)), Json<Dictionary<string, string>>(r.GetString(4)),
        Json<Dictionary<string, IReadOnlyList<string>>>(r.GetString(5)), r.Get<Guid?>("category_id"), Json<ServiceAudience>(r.GetString(7)),
        r.Get<Guid?>("owner_org_unit_id"), r.GetString(9), r.Get<string?>("url"), r.GetBoolean(11), r.Get<string?>("launch_route"),
        r.GetString(13), r.Get<int?>("current_version"), r.GetFieldValue<DateTimeOffset>(15), r.Get<string?>("icon"));

    internal static T Json<T>(string json) => JsonSerializer.Deserialize<T>(json, JsonDefaults.Options)!;

    public async Task<AudienceFacts> ViewerAsync(CancellationToken ct)
    {
        if (viewer is not null)
        {
            return viewer;
        }
        return viewer = await FactsAsync(context.RequireUser(), ct);
    }

    /// <summary>The audience facts of any member of the tenant, for example the person a request is filed for.</summary>
    public async Task<AudienceFacts> FactsAsync(Guid user, CancellationToken ct)
    {
        var units = new HashSet<Guid>();
        foreach (var m in await hierarchy.GetActiveMembershipsAsync(user, ct))
        {
            units.UnionWith(await hierarchy.GetAncestorsAndSelfAsync(m.OrgUnitId, ct));
        }
        return new AudienceFacts(units, await groups.GroupIdsOfAsync(user, ct), (await memberships.GetAsync(user, ct))?.MembershipType);
    }

    /// <summary>Adds module services that are not in the catalogue yet and keeps their names in step with the module.</summary>
    internal async Task SyncModuleServicesAsync(CancellationToken ct)
    {
        foreach (var registration in moduleSources.SelectMany(s => s.Services))
        {
            await session.ExecAsync(
                """
                insert into services.service (tenant_id, id, kind, module_key, names, descriptions, launch_route, icon, status, created_at, updated_at)
                values (platform.current_tenant_id(), @id, 'module', @key, @names, @descriptions, @route, @icon, 'published', @now, @now)
                on conflict (tenant_id, module_key) where module_key is not null do update
                  set names = excluded.names, descriptions = excluded.descriptions, launch_route = excluded.launch_route,
                      icon = coalesce(services.service.icon, excluded.icon)
                  where services.service.names <> excluded.names or services.service.descriptions <> excluded.descriptions
                     or services.service.launch_route is distinct from excluded.launch_route
                     or (services.service.icon is null and excluded.icon is not null)
                """, new
                {
                    id = Ids.New(), key = registration.Key, names = Jsonb.From(registration.Names),
                    descriptions = Jsonb.From(registration.Descriptions ?? new Dictionary<string, string>()), route = registration.LaunchRoute,
                    icon = registration.Icon, now = clock.UtcNow,
                }, ct);
        }
    }

    public async Task<IReadOnlyList<CategoryView>> CategoriesAsync(CancellationToken ct) =>
        await session.QueryAsync("select id, names::text, position from services.category order by position, id", null,
            r => new CategoryView(r.GetGuid(0), Json<Dictionary<string, string>>(r.GetString(1)), r.GetInt32(2)), ct);

    private static ServiceCard Card(ServiceRow s, bool favourite) =>
        new(s.Id, s.Kind, s.Names, s.Descriptions, s.CategoryId, s.Kind == ServiceKinds.Linked ? s.Url : null, s.Kind == ServiceKinds.Linked && s.Sso,
            s.Kind == ServiceKinds.Module ? s.LaunchRoute : null, favourite, s.Icon);

    /// <summary>
    /// What a card says before the service is opened (FR-019): for each published form service, who approves and the
    /// working days its approval step allows; and the viewer's latest request of each service. One read each.
    /// </summary>
    private async Task<IReadOnlyList<ServiceCard>> WithDetailsAsync(Guid? person, IReadOnlyList<ServiceCard> cards, CancellationToken ct)
    {
        var ids = cards.Select(c => c.Id).ToArray();
        if (ids.Length == 0)
        {
            return cards;
        }
        var routes = (await session.QueryAsync(
            """
            select s.id, v.route::text from services.service s
            join services.service_version v on v.service_id = s.id and v.version = s.current_version
            where s.id = any(@ids) and s.kind = 'form'
            """, new { ids }, r => (Id: r.GetGuid(0), Route: Json<ServiceRoute>(r.GetString(1))), ct)).ToDictionary(x => x.Id, x => x.Route);
        // An app calling without a person (spec 035) has no requests of its own.
        var latest = person is { } user
            ? (await session.QueryAsync(
                """
                select distinct on (service_id) service_id, id, number, status from services.request
                where service_id = any(@ids) and status <> 'draft' and (requester_user_id = @user or on_behalf_of_user_id = @user)
                order by service_id, created_at desc, id desc
                """, new { ids, user }, r => (ServiceId: r.GetGuid(0), Request: new LatestRequest(r.GetGuid(1), r.GetString(2), r.GetString(3))), ct))
                .ToDictionary(x => x.ServiceId, x => x.Request)
            : [];
        return cards.Select(c => routes.TryGetValue(c.Id, out var route)
                ? c with { Approver = ServiceApprovers.Of(route), DueWorkingDays = route.Approval?.DueWorkingDays, Latest = latest.GetValueOrDefault(c.Id) }
                : c with { Latest = latest.GetValueOrDefault(c.Id) })
            .ToList();
    }

    /// <summary>Module services whose capability the tenant's plan does not have.</summary>
    private async Task<IReadOnlySet<string>> UnavailableModuleKeysAsync(CancellationToken ct)
    {
        var off = new HashSet<string>(StringComparer.Ordinal);
        foreach (var registration in moduleSources.SelectMany(s => s.Services).Where(r => r.Capability is not null))
        {
            if (!await entitlements.IsEnabledAsync(registration.Capability!, ct))
            {
                off.Add(registration.Key);
            }
        }
        return off;
    }

    public async Task<CatalogueView> ListAsync(string? query, Guid? categoryId, CancellationToken ct)
    {
        await entitlements.DemandAsync(Capabilities.WorkflowCatalogue, ct);
        var person = context.ActorType == ActorType.App ? (Guid?)null : context.RequireUser();
        await SyncModuleServicesAsync(ct);
        var favourites = person is { } user
            ? (await session.QueryAsync("select service_id from services.favourite where user_id = @user", new { user }, r => r.GetGuid(0), ct)).ToHashSet()
            : [];
        var unavailable = await UnavailableModuleKeysAsync(ct);
        var visible = new List<ServiceRow>();
        foreach (var s in await session.QueryAsync($"select {Columns} from services.service where status = 'published'", null, Read, ct))
        {
            if ((s.ModuleKey is null || !unavailable.Contains(s.ModuleKey)) && await OffersAsync(s.Audience, ct))
            {
                visible.Add(s);
            }
        }
        var terms = Search.Terms(query);
        var shown = visible
            .Where(s => categoryId is null || s.CategoryId == categoryId)
            .Where(s => terms.Count == 0 || Search.Matches(terms, s))
            .OrderBy(s => s.Names.Values.FirstOrDefault(), StringComparer.CurrentCulture)
            .Select(s => Card(s, favourites.Contains(s.Id))).ToList();
        shown = (await WithDetailsAsync(person, shown, ct)).ToList();
        var ids = visible.Select(s => s.Id).ToHashSet();
        IReadOnlyList<Guid> recent = person is { } me
            ? (await session.QueryAsync("select service_id from services.recent_use where user_id = @me order by used_at desc limit 50", new { me },
                r => r.GetGuid(0), ct)).Where(ids.Contains).Take(RecentCount).ToList()
            : [];
        return new CatalogueView(await CategoriesAsync(ct), shown, recent);
    }

    /// <summary>
    /// One service for the viewer. A service that exists but is not offered to them is refused with "not available to
    /// you" (US1 scenario 4); a draft, a retired service or one of another tenant is not found.
    /// </summary>
    public async Task<(ServiceRow Row, ServiceCard Card)> VisibleAsync(Guid id, CancellationToken ct)
    {
        await entitlements.DemandAsync(Capabilities.WorkflowCatalogue, ct);
        var app = context.ActorType == ActorType.App;
        var user = app ? Guid.Empty : context.RequireUser();
        await SyncModuleServicesAsync(ct);
        var row = await session.QuerySingleOrDefaultAsync($"select {Columns} from services.service where id = @id and status = 'published'", new { id }, Read, ct)
            ?? throw AppException.NotFound();
        if (row.ModuleKey is { } moduleKey && (await UnavailableModuleKeysAsync(ct)).Contains(moduleKey))
        {
            throw AppException.NotFound();
        }
        if (!await OffersAsync(row.Audience, ct))
        {
            // An app is told nothing about a service outside its reach (spec 035: scopes never grant access on their own).
            throw app ? AppException.NotFound() : AppException.Denied("services.not_available");
        }
        var favourite = !app && await session.ScalarAsync<bool>("select exists (select 1 from services.favourite where user_id = @user and service_id = @id)", new { user, id }, ct);
        return (row, Card(row, favourite));
    }

    public async Task<ServiceDetail> DetailAsync(Guid id, CancellationToken ct)
    {
        var (row, card) = await VisibleAsync(id, ct);
        if (row.CurrentVersion is not { } version)
        {
            return new ServiceDetail(card, null, FormDefinition.Empty, false, 0, false, row.Classification);
        }
        var v = await Versions.GetAsync(session, id, version, ct) ?? throw AppException.NotFound();
        return new ServiceDetail(card, version, v.Form, v.AllowOnBehalf, v.MaxAttachments, v.Route.Approval is not null || v.Route.Graph is not null, row.Classification);
    }

    public async Task SetFavouriteAsync(Guid id, bool favourite, CancellationToken ct)
    {
        var user = context.RequireUser();
        if (favourite)
        {
            await VisibleAsync(id, ct);
            await session.ExecAsync(
                "insert into services.favourite (tenant_id, user_id, service_id, created_at) values (platform.current_tenant_id(), @user, @id, @now) on conflict do nothing",
                new { user, id, now = clock.UtcNow }, ct);
            return;
        }
        if (!await session.ScalarAsync<bool>("select exists (select 1 from services.service where id = @id)", new { id }, ct))
        {
            throw AppException.NotFound();
        }
        await session.ExecAsync("delete from services.favourite where user_id = @user and service_id = @id", new { user, id }, ct);
    }

    /// <summary>Records that the viewer used a service (opened a linked app, submitted a request) for the recent list.</summary>
    public async Task MarkUsedAsync(Guid id, CancellationToken ct)
    {
        var user = context.RequireUser();
        await VisibleAsync(id, ct);
        await session.ExecAsync(
            """
            insert into services.recent_use (tenant_id, user_id, service_id, used_at) values (platform.current_tenant_id(), @user, @id, @now)
            on conflict (tenant_id, user_id, service_id) do update set used_at = excluded.used_at
            """, new { user, id, now = clock.UtcNow }, ct);
    }
}

/// <summary>Plain text search over names, descriptions and synonyms in every locale (US1). Spec 012 adds global search.</summary>
public static class Search
{
    public static IReadOnlyList<string> Terms(string? query) =>
        string.IsNullOrWhiteSpace(query) ? [] : Normalize(query).Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Take(8).ToList();

    public static bool Matches(IReadOnlyList<string> terms, ServiceRow s)
    {
        var text = Normalize(string.Join(' ', s.Names.Values.Concat(s.Descriptions.Values).Concat(s.Synonyms.Values.SelectMany(v => v))));
        return terms.All(t => text.Contains(t, StringComparison.Ordinal));
    }

    /// <summary>Lower case, Arabic diacritics and tatweel removed, and alef, yeh and teh marbuta forms folded.</summary>
    public static string Normalize(string text)
    {
        var chars = new System.Text.StringBuilder(text.Length);
        foreach (var c in text.ToLowerInvariant())
        {
            if (c is >= 'ً' and <= 'ٟ' or 'ٰ' or 'ـ')
            {
                continue;
            }
            chars.Append(c switch
            {
                'آ' or 'أ' or 'إ' => 'ا',
                'ى' => 'ي',
                'ة' => 'ه',
                _ => c,
            });
        }
        return chars.ToString();
    }
}
