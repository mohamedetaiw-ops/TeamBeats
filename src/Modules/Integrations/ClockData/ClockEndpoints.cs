using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using TeamBeats.Authorization.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Primitives;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.Integrations.ClockData;

/// <summary>
/// Clock data files of a connection (spec 017 US7, permission <c>integration.manage</c>): the files imported with their
/// counts, the rows that were not imported, an import now, and a CSV upload (the request body, up to 20 MB).
/// </summary>
public static class ClockEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api/v1/integrations/pack-connections/{id:guid}").RequireTenantUser().InAdminArea()
            .WithTags("Integrations: connector packs");

        api.MapGet("/clock-files", async (Guid id, string? cursor, IAuthorizationService authz, ClockImports imports, CancellationToken ct) =>
        {
            await Demand(authz, ct);
            return Results.Ok(await imports.FilesAsync(id, cursor, ct));
        });

        api.MapGet("/clock-files/{fileId:guid}/unmatched", async (Guid id, Guid fileId, IAuthorizationService authz, ClockImports imports, CancellationToken ct) =>
        {
            await Demand(authz, ct);
            return Results.Ok(await imports.ProblemsAsync(id, fileId, ct));
        });

        api.MapPost("/clock-files:import", async (Guid id, IAuthorizationService authz, ClockImports imports, IDbSession session, CancellationToken ct) =>
        {
            await Demand(authz, ct);
            var result = await imports.ImportNowAsync(id, ct);
            await session.CommitAsync(ct);
            return Results.Accepted(value: result);
        });

        api.MapPost("/clock-files", async (Guid id, string? name, HttpRequest request, IAuthorizationService authz, ClockImports imports, IDbSession session,
            CancellationToken ct) =>
        {
            await Demand(authz, ct);
            if (request.ContentLength is > ClockImports.MaxUploadBytes)
            {
                throw AppException.Validation("file", "validation.clock.file_too_large");
            }
            using var buffer = new MemoryStream();
            var chunk = new byte[81920];
            int read;
            while ((read = await request.Body.ReadAsync(chunk, ct)) > 0)
            {
                if (buffer.Length + read > ClockImports.MaxUploadBytes)
                {
                    throw AppException.Validation("file", "validation.clock.file_too_large");
                }
                buffer.Write(chunk, 0, read);
            }
            var view = await imports.UploadAsync(id, name, buffer.ToArray(), ct);
            await session.CommitAsync(ct);
            return Results.Created($"/api/v1/integrations/pack-connections/{id}/clock-files/{view.Id}", view);
        });
    }

    private static Task Demand(IAuthorizationService authz, CancellationToken ct) => authz.DemandAsync(Permissions.IntegrationManage, AuthzTarget.Tenant, ct);
}
