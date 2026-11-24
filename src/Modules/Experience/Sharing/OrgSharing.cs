using TeamBeats.Audit.Contracts;
using TeamBeats.Authorization.Contracts;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Primitives;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.Experience.Sharing;

/// <summary>One organization a page or organization menu is shared with, and the organizations below it.</summary>
public sealed record ShareView(Guid OrgUnitId, string Name, Guid SharedBy, DateTimeOffset SharedAt);

/// <summary>
/// Sharing policies (spec 033 R2 T019, US4 scenarios 4 and 5, FR-021, FR-022). A page owned by an organization reaches
/// people on its own branch: that organization, the organizations below it and the organizations above it. People of
/// another branch (Acme Africa for an Acme Gulf page) see it only when its owner shares it with their organization, which
/// needs the owner's publish right (page.publish for a page, navigation.edit for a menu). People who may edit it at its scope always see it.
/// Sharing only decides what appears; the central authorization service still decides every permission, so nothing is
/// merged. Tenant pages and the global menu reach everyone, as before. Applications reading through the public API are
/// held by their own role scope only.
/// </summary>
public sealed class OrgSharing(
    IDbSession session,
    ITenantContext context,
    IAuthorizationService authz,
    IOrganizationHierarchy hierarchy,
    IAuditWriter audit,
    IClock clock)
{
    public const string Page = "page";
    public const string Menu = "menu";

    private IReadOnlySet<Guid>? viewerLine;
    private Dictionary<(string, Guid), IReadOnlyList<Guid>>? shares;
    private readonly Dictionary<(string, Guid), bool> editors = [];
    private readonly Dictionary<Guid, IReadOnlyList<Guid>> ownerPaths = [];
    private IReadOnlySet<Guid>? viewerUnits;

    /// <summary>Whether the viewer sees a page or menu of this owner; <paramref name="owner"/> null is the tenant.</summary>
    public async Task<bool> SeesAsync(string kind, Guid resourceId, Guid? owner, CancellationToken ct)
    {
        if (owner is not { } unit || context.ActorType != ActorType.User)
        {
            return true;
        }
        var line = await ViewerLineAsync(ct);
        if (await OnBranchAsync(unit, ct))
        {
            return true;
        }
        var granted = await SharesOfAsync(kind, resourceId, ct);
        if (granted.Any(line.Contains))
        {
            return true;
        }
        return await EditsAsync(kind, unit, ct);
    }

    /// <summary>Whether the owner shared it with one of the viewer's organizations or one above them; editing rights do not count.</summary>
    public async Task<bool> SharedWithViewerAsync(string kind, Guid resourceId, CancellationToken ct)
    {
        if (context.ActorType != ActorType.User)
        {
            return false;
        }
        var line = await ViewerLineAsync(ct);
        return (await SharesOfAsync(kind, resourceId, ct)).Any(line.Contains);
    }

    /// <summary>
    /// Whether the viewer reaches it through a share from another branch, so it shows its owner (FR-022). People of the
    /// owning branch know whose it is, and an editor reaching it through their rights alone was not shared anything.
    /// </summary>
    public async Task<bool> IsSharedToViewerAsync(string kind, Guid resourceId, Guid? owner, CancellationToken ct) =>
        owner is { } unit && context.ActorType == ActorType.User && !await OnBranchAsync(unit, ct) && await SharedWithViewerAsync(kind, resourceId, ct);

    /// <summary>Whether the owner is one of the viewer's organizations, above one of them or below one of them.</summary>
    private async Task<bool> OnBranchAsync(Guid owner, CancellationToken ct)
    {
        if ((await ViewerLineAsync(ct)).Contains(owner))
        {
            return true;
        }
        if (!ownerPaths.TryGetValue(owner, out var path))
        {
            path = ownerPaths[owner] = (await hierarchy.GetPathsAsync([owner], ct)).GetValueOrDefault(owner) ?? [];
        }
        var own = await ViewerUnitsAsync(ct);
        return path.Any(own.Contains);
    }

    /// <summary>
    /// The organization that owns a page, or the organization of an organization menu. A tenant page, the global menu, an
    /// unknown kind and anything of another tenant are not found: only an organization's own pages and menus are shared.
    /// </summary>
    public async Task<Guid> OwnerAsync(string kind, Guid resourceId, CancellationToken ct)
    {
        Guid? owner = kind switch
        {
            Page => await session.ScalarAsync<Guid?>("select org_unit_id from experience.page where id = @resourceId", new { resourceId }, ct),
            Menu => await session.ScalarAsync<long>(
                "select count(*) from experience.nav_menu where kind = 'organization' and scope = @scope", new { scope = resourceId.ToString() }, ct) > 0
                ? resourceId
                : null,
            _ => null,
        };
        return owner ?? throw AppException.NotFound();
    }

    /// <summary>The organizations a page or menu is shared with, for whoever may change its sharing.</summary>
    public async Task<IReadOnlyList<ShareView>> ListAsync(string kind, Guid resourceId, Guid owner, string locale, CancellationToken ct)
    {
        await DemandManageAsync(kind, owner, ct);
        var rows = await session.QueryAsync(
            "select org_unit_id, shared_by, shared_at from experience.experience_share where kind = @kind and resource_id = @resourceId order by shared_at",
            new { kind, resourceId }, r => (Unit: r.GetGuid(0), By: r.GetGuid(1), At: r.GetFieldValue<DateTimeOffset>(2)), ct);
        var names = await hierarchy.GetNamesAsync(rows.Select(r => r.Unit), ct);
        return rows.Select(r => new ShareView(r.Unit, NameOf(names, r.Unit, locale), r.By, r.At)).ToList();
    }

    /// <summary>Shares with an organization (and the organizations below it). Sharing again changes nothing.</summary>
    public async Task ShareAsync(string kind, Guid resourceId, Guid owner, Guid unit, CancellationToken ct)
    {
        await DemandManageAsync(kind, owner, ct);
        if (!await hierarchy.UnitExistsAsync(unit, ct))
        {
            throw AppException.Validation("orgUnitId", "validation.experience.share_unit");
        }
        if (await hierarchy.IsAncestorOrSelfAsync(owner, unit, ct) || await hierarchy.IsAncestorOrSelfAsync(unit, owner, ct))
        {
            // The owner's own branch sees it already; a share there would mean nothing.
            throw AppException.Validation("orgUnitId", "validation.experience.share_own_line");
        }
        var actor = context.ActorId ?? throw AppException.Denied();
        var added = await session.ExecAsync(
            """
            insert into experience.experience_share (tenant_id, kind, resource_id, org_unit_id, shared_by, shared_at)
            values (platform.current_tenant_id(), @kind, @resourceId, @unit, @actor, @now)
            on conflict do nothing
            """, new { kind, resourceId, unit, actor, now = clock.UtcNow }, ct);
        if (added > 0)
        {
            await audit.WriteAsync(new AuditEntry(AuditActions.ExperienceShareChanged, kind, resourceId.ToString(), TargetOrgUnitId: owner,
                Changes: new { shared = unit }), ct);
        }
    }

    public async Task UnshareAsync(string kind, Guid resourceId, Guid owner, Guid unit, CancellationToken ct)
    {
        await DemandManageAsync(kind, owner, ct);
        var removed = await session.ExecAsync(
            "delete from experience.experience_share where kind = @kind and resource_id = @resourceId and org_unit_id = @unit",
            new { kind, resourceId, unit }, ct);
        if (removed == 0)
        {
            throw AppException.NotFound();
        }
        await audit.WriteAsync(new AuditEntry(AuditActions.ExperienceShareChanged, kind, resourceId.ToString(), TargetOrgUnitId: owner,
            Changes: new { unshared = unit }), ct);
    }

    /// <summary>Removes every share of a page or menu that is gone.</summary>
    public Task ClearAsync(string kind, Guid resourceId, CancellationToken ct) =>
        session.ExecAsync("delete from experience.experience_share where kind = @kind and resource_id = @resourceId", new { kind, resourceId }, ct);

    private async Task DemandManageAsync(string kind, Guid owner, CancellationToken ct) =>
        await authz.DemandVisibleAsync(ManagePermission(kind), AuthzTarget.OrgUnit(owner), ct);

    private static string ManagePermission(string kind) => kind switch
    {
        Page => Permissions.PagePublish,
        Menu => Permissions.NavigationEdit,
        _ => throw AppException.NotFound(),
    };

    private static string EditPermission(string kind) => kind == Page ? Permissions.PageEdit : Permissions.NavigationEdit;

    private async Task<bool> EditsAsync(string kind, Guid owner, CancellationToken ct)
    {
        if (!editors.TryGetValue((kind, owner), out var allowed))
        {
            allowed = editors[(kind, owner)] = (await authz.CheckAsync(EditPermission(kind), AuthzTarget.OrgUnit(owner), ct)).Allowed;
        }
        return allowed;
    }

    /// <summary>The viewer's own organizations.</summary>
    private async Task<IReadOnlySet<Guid>> ViewerUnitsAsync(CancellationToken ct)
    {
        if (viewerUnits is null)
        {
            var memberships = await hierarchy.GetActiveMembershipsAsync(context.ActorId ?? throw AppException.Denied(), ct);
            viewerUnits = memberships.Select(m => m.OrgUnitId).ToHashSet();
        }
        return viewerUnits;
    }

    /// <summary>The viewer's organizations and the organizations above them; a share with any of them reaches the viewer.</summary>
    private async Task<IReadOnlySet<Guid>> ViewerLineAsync(CancellationToken ct)
    {
        if (viewerLine is null)
        {
            var paths = await hierarchy.GetPathsAsync(await ViewerUnitsAsync(ct), ct);
            viewerLine = paths.Values.SelectMany(p => p).ToHashSet();
        }
        return viewerLine;
    }

    private async Task<IReadOnlyList<Guid>> SharesOfAsync(string kind, Guid resourceId, CancellationToken ct)
    {
        if (shares is null)
        {
            // One read per request: every share of the tenant, which is small (a few per page or menu at most).
            var rows = await session.QueryAsync("select kind, resource_id, org_unit_id from experience.experience_share", null,
                r => (Kind: r.GetString(0), Resource: r.GetGuid(1), Unit: r.GetGuid(2)), ct);
            shares = rows.GroupBy(r => (r.Kind, r.Resource)).ToDictionary(g => g.Key, g => (IReadOnlyList<Guid>)g.Select(r => r.Unit).ToList());
        }
        return shares.TryGetValue((kind, resourceId), out var units) ? units : [];
    }

    private static string NameOf(IReadOnlyDictionary<Guid, IReadOnlyDictionary<string, string>> names, Guid unit, string locale) =>
        names.TryGetValue(unit, out var byLocale)
            ? byLocale.GetValueOrDefault(locale) ?? byLocale.GetValueOrDefault(Locales.Fallback) ?? byLocale.Values.FirstOrDefault() ?? unit.ToString()
            : unit.ToString();
}
