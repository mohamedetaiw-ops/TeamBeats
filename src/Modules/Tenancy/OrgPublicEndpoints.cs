using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using TeamBeats.Localization;
using TeamBeats.Modules.PublicApi.Contracts;
using TeamBeats.Modules.Tenancy.Contracts;

namespace TeamBeats.Modules.Tenancy;

/// <summary>An organization unit in the public API: identifiers and codes as stored, the name in the caller's language.</summary>
public sealed record PublicOrgUnit(Guid Id, Guid? ParentId, string Type, string Code, string Name, int Level, string Status);

/// <summary>
/// The public operations of <c>org:read</c> (spec 035 section 6, spec 002): organization units and their hierarchy. Each
/// call asks the central authorization service for <c>org.unit_view</c>, for the person or for the app's own roles, so an
/// app with a role at one subsidiary sees that subsidiary only. Units the caller may not see answer not found.
/// </summary>
public static class OrgPublicEndpoints
{
    private sealed record Position(int L, string C, Guid I);

    public static void MapOrgPublicEndpoints(this IEndpointRouteBuilder app)
    {
        var org = app.MapPublicApi("Public: organization");
        org.MapGet("/org-units", async (Guid? parentId, string? cursor, int? limit, OrgHierarchyService service, RequestLocale locale,
            ITenantSettingsReader settings, CancellationToken ct) =>
        {
            var size = PublicEndpoints.PageSize(limit);
            var after = PublicEndpoints.DecodeCursor<Position>(cursor);
            var fallback = (await settings.GetLocaleSettingsAsync(ct)).DefaultLocale;
            var units = (await service.ListAsync(parentId, null, ct))
                .OrderBy(u => u.Level).ThenBy(u => u.Code, StringComparer.Ordinal).ThenBy(u => u.Id)
                .Where(u => after is null || Compare(u, after) > 0)
                .Take(size + 1)
                .ToList();
            var page = units.Take(size).Select(u => View(u, locale.Locale, fallback)).ToList();
            var next = units.Count > size ? PublicEndpoints.EncodeCursor(new Position(units[size - 1].Level, units[size - 1].Code, units[size - 1].Id)) : null;
            return Results.Ok(new PublicPage<PublicOrgUnit>(page, next));
        }).RequireScope(ScopeCatalogue.OrgRead).Produces<PublicPage<PublicOrgUnit>>();

        org.MapGet("/org-units/{id:guid}", async (Guid id, HttpContext http, OrgHierarchyService service, RequestLocale locale,
            ITenantSettingsReader settings, CancellationToken ct) =>
        {
            var unit = await service.GetAsync(id, ct);
            return PublicEndpoints.Resource(http, View(unit, locale.Locale, (await settings.GetLocaleSettingsAsync(ct)).DefaultLocale));
        }).RequireScope(ScopeCatalogue.OrgRead).Produces<PublicOrgUnit>();
    }

    private static int Compare(OrgUnitView unit, Position after)
    {
        var level = unit.Level.CompareTo(after.L);
        if (level != 0)
        {
            return level;
        }
        var code = string.CompareOrdinal(unit.Code, after.C);
        return code != 0 ? code : unit.Id.CompareTo(after.I);
    }

    private static PublicOrgUnit View(OrgUnitView unit, string locale, string fallback) => new(unit.Id, unit.ParentId, unit.TypeKey, unit.Code,
        unit.Names.TryGetValue(locale, out var name) ? name : unit.Names.TryGetValue(fallback, out var other) ? other : unit.Code, unit.Level, unit.Status);
}

/// <summary>Whether the principal in context may read an organization unit through the public API (spec 035 FR-018), by the endpoint's own read.</summary>
public sealed class OrgUnitVisibility(OrgHierarchyService units) : PublicReadVisibility
{
    public override string ResourceType => PublicResourceTypes.OrgUnit;

    protected override Task ReadAsync(Guid resourceId, CancellationToken ct) => units.GetAsync(resourceId, ct);
}
