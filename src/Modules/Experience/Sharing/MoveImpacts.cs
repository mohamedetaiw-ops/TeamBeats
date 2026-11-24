using TeamBeats.Authorization.Contracts;
using TeamBeats.Modules.Experience.Themes;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Primitives;

namespace TeamBeats.Modules.Experience.Sharing;

public sealed record MoveImpactUnit(Guid Id, IReadOnlyDictionary<string, string> Names);

/// <summary>
/// What moving an organization under another parent changes for its people (spec 033 US5, FR-025): the theme, the
/// organization pages and menus of the organizations above it that stop or start reaching it, and how many
/// organizations move with it. Pages shared with it on purpose keep reaching it. Lists stop at 50 entries.
/// </summary>
public sealed record MoveImpact(
    Guid OrgUnitId,
    Guid? NewParentId,
    int Units,
    ThemeMoveImpact Theme,
    int PagesLost,
    int PagesGained,
    IReadOnlyList<MoveImpactUnit> MenusLost,
    IReadOnlyList<MoveImpactUnit> MenusGained);

/// <summary>
/// Experience impact of an organization move, shown before the move is confirmed (spec 002 moves the unit). Nothing is
/// copied or changed here: after the move the organization inherits from its new parents only, and what belonged to the
/// old ones stops reaching it, which is what this report lists. It needs the right to make the move.
/// </summary>
public sealed class MoveImpacts(IDbSession session, IAuthorizationService authz, IOrganizationHierarchy hierarchy, ThemeService themes)
{
    public async Task<MoveImpact> ForAsync(Guid unit, Guid? newParent, CancellationToken ct)
    {
        if (!await hierarchy.UnitExistsAsync(unit, ct) || (newParent is { } np && !await hierarchy.UnitExistsAsync(np, ct)))
        {
            throw AppException.NotFound();
        }
        await authz.DemandVisibleAsync(Permissions.OrgUnitManage, AuthzTarget.OrgUnit(unit), ct);
        await authz.DemandAsync(Permissions.OrgUnitManage, newParent is { } p ? AuthzTarget.OrgUnit(p) : AuthzTarget.Tenant, ct);
        var subtree = await hierarchy.GetSubtreesAsync([unit], ct);
        if (newParent is { } target && subtree.Contains(target))
        {
            throw AppException.Validation("newParentId", "validation.org.cycle");
        }

        var oldAbove = (await hierarchy.GetAncestorsAndSelfAsync(unit, ct)).Where(u => u != unit).ToHashSet();
        var newAbove = newParent is { } q ? (await hierarchy.GetAncestorsAndSelfAsync(q, ct)).ToHashSet() : [];
        var leaving = oldAbove.Except(newAbove).ToArray();
        var joining = newAbove.Except(oldAbove).ToArray();
        // A share to the moved organization, one below it or one of its new parents still covers it after the move.
        var covering = subtree.Concat(newAbove).ToArray();

        var pagesLost = leaving.Length == 0 ? 0 : (int)await session.ScalarAsync<long>(
            """
            select count(*) from experience.page p
            where p.org_unit_id = any(@leaving)
              and not exists (select 1 from experience.experience_share s where s.kind = 'page' and s.resource_id = p.id and s.org_unit_id = any(@covering))
            """, new { leaving, covering }, ct);
        var pagesGained = joining.Length == 0 ? 0 : (int)await session.ScalarAsync<long>(
            "select count(*) from experience.page where org_unit_id = any(@joining)", new { joining }, ct);
        var menuScopes = (await session.QueryAsync("select scope from experience.nav_menu where kind = 'organization'", null, r => r.GetString(0), ct))
            .Select(s => Guid.TryParse(s, out var g) ? g : Guid.Empty).ToHashSet();
        var menusLost = leaving.Where(menuScopes.Contains).Take(50).ToList();
        var menusGained = joining.Where(menuScopes.Contains).Take(50).ToList();
        var names = await hierarchy.GetNamesAsync(menusLost.Concat(menusGained), ct);
        IReadOnlyList<MoveImpactUnit> Named(IEnumerable<Guid> ids) =>
            ids.Select(id => new MoveImpactUnit(id, names.GetValueOrDefault(id) ?? new Dictionary<string, string>())).ToList();

        return new MoveImpact(unit, newParent, subtree.Count, await themes.MoveImpactAsync(unit, newParent, ct), pagesLost, pagesGained,
            Named(menusLost), Named(menusGained));
    }
}
