using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using TeamBeats.Modules.PublicApi.Contracts;

namespace TeamBeats.Modules.Authorization;

/// <summary>A role as the admin API shows it: its key, whether it is built in, and its permissions.</summary>
public sealed record PublicRole(string Key, bool BuiltIn, IReadOnlyList<string> Permissions);

/// <summary>A role assignment as the admin API shows it. Conditions stay in the admin area.</summary>
public sealed record PublicRoleAssignment(Guid Id, string SubjectType, Guid SubjectId, string RoleKey, string ScopeType, Guid? ScopeId, DateTimeOffset CreatedAt);

/// <summary>
/// Admin APIs beside SCIM (spec 035 R3, T023): role administration read by a person acting through a delegated app, for
/// access reviews and identity governance tools. SCIM (spec 003) provisions people; these operations show the roles and
/// who holds them. They run the same use cases as the admin area with the person's own rights, so only what the person
/// may see in TeamBeats comes back; the scope needs an administrator's consent. Nothing here changes a role (FR-012),
/// and apps' own roles are left out.
/// </summary>
public static class AuthorizationPublicEndpoints
{
    private sealed record Position(Guid After);

    public static void MapAuthorizationPublicEndpoints(this IEndpointRouteBuilder app)
    {
        var admin = app.MapPublicApi("Public: role administration");

        admin.MapGet("/admin/roles", async (AuthorizationAdmin authz, CancellationToken ct) =>
            Results.Ok((await authz.ListRolesAsync(ct)).Select(r => new PublicRole(r.Key, r.IsBuiltIn, r.Permissions)).ToList()))
            .RequireScope(ScopeCatalogue.RolesRead).Produces<IReadOnlyList<PublicRole>>();

        admin.MapGet("/admin/role-assignments", async (Guid? userId, string? cursor, int? limit, AuthorizationAdmin authz, CancellationToken ct) =>
        {
            var size = PublicEndpoints.PageSize(limit);
            var after = PublicEndpoints.DecodeCursor<Position>(cursor)?.After;
            var all = (await authz.ListAssignmentsAsync(userId, ct)).Where(a => a.SubjectType is "user" or "group").ToList();
            var start = after is { } id ? all.FindIndex(a => a.Id == id) + 1 : 0;
            var page = all.Skip(start).Take(size).Select(View).ToList();
            var next = start + size < all.Count ? PublicEndpoints.EncodeCursor(new Position(page[^1].Id)) : null;
            return Results.Ok(new PublicPage<PublicRoleAssignment>(page, next));
        }).RequireScope(ScopeCatalogue.RolesRead).Produces<PublicPage<PublicRoleAssignment>>();
    }

    private static PublicRoleAssignment View(AssignmentView a) => new(a.Id, a.SubjectType, a.SubjectId, a.RoleKey, a.ScopeType, a.ScopeId, a.CreatedAt);
}
