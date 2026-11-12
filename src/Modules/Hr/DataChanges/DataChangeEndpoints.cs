using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using TeamBeats.Entitlements.Contracts;
using TeamBeats.Persistence;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.Hr.DataChanges;

/// <summary>Data change endpoints (spec 022 slice 5). Every endpoint needs <c>hr.data_change</c> in the plan.</summary>
public static class DataChangeEndpoints
{
    private static string? IfMatch(HttpRequest http) => http.Headers.IfMatch.ToString() is { Length: > 0 } tag ? tag : null;

    public static IEndpointRouteBuilder MapDataChangeEndpoints(this IEndpointRouteBuilder app)
    {
        var me = app.MapGroup("/api/v1/me/data-changes").RequireTenantUser().RequiresCapability(Capabilities.HrDataChange).WithTags("Data changes");
        me.MapGet("", async (DataChangeRequests requests, CancellationToken ct) => Results.Ok(await requests.MineAsync(ct)));
        me.MapGet("/groups", async (DataChangeConfig config, CancellationToken ct) => Results.Ok(await config.GroupsAsync(ct)));
        me.MapPost("", async (DataChangeInput input, DataChangeRequests requests, IDbSession session, CancellationToken ct) =>
        {
            var view = await requests.CreateAsync(input, ct);
            await session.CommitAsync(ct);
            return Results.Created($"/api/v1/hr/data-changes/{view.Id}", view);
        });
        me.MapPost("/documents", async (string? name, HttpRequest http, DataChangeConfig config, IDbSession session, CancellationToken ct) =>
        {
            var buffer = new byte[DataChangeConfig.MaxBytes + 1];
            int read, total = 0;
            while ((read = await http.Body.ReadAsync(buffer.AsMemory(total, buffer.Length - total), ct)) > 0)
            {
                total += read;
                if (total > DataChangeConfig.MaxBytes)
                {
                    throw Primitives.AppException.Validation("file", "validation.data_change.document_size");
                }
            }
            var view = await config.UploadAsync(name, http.ContentType, buffer[..total], ct);
            await session.CommitAsync(ct);
            return Results.Created($"/api/v1/me/data-changes/documents/{view.Id}", view);
        }).DisableAntiforgery();

        var hr = app.MapGroup("/api/v1/hr").RequireTenantUser().RequiresCapability(Capabilities.HrDataChange).WithTags("Data changes");
        hr.MapGet("/data-change-settings", async (DataChangeConfig config, HttpResponse response, CancellationToken ct) =>
        {
            var view = await config.SettingsAsync(ct);
            response.Headers.ETag = view.ETag;
            return Results.Ok(view);
        }).InAdminArea();
        hr.MapPut("/data-change-settings", async (DataChangeSettingsRequest request, HttpRequest http, DataChangeConfig config, IDbSession session, CancellationToken ct) =>
        {
            var view = await config.UpdateSettingsAsync(IfMatch(http), request, ct);
            await session.CommitAsync(ct);
            http.HttpContext.Response.Headers.ETag = view.ETag;
            return Results.Ok(view);
        }).InAdminArea();
        hr.MapGet("/data-changes", async (string? status, DataChangeRequests requests, CancellationToken ct) => Results.Ok(await requests.ListAsync(status, ct)))
            .InAdminArea();
        hr.MapGet("/data-changes/{id:guid}", async (Guid id, DataChangeRequests requests, HttpResponse response, CancellationToken ct) =>
        {
            response.Headers.CacheControl = "no-store";
            return Results.Ok(await requests.GetAsync(id, ct));
        });
        hr.MapGet("/data-changes/{id:guid}/document", async (Guid id, DataChangeRequests requests, HttpResponse response, CancellationToken ct) =>
        {
            var (content, type, name) = await requests.DocumentAsync(id, ct);
            response.Headers.CacheControl = "no-store";
            return Results.File(content, type, name);
        });
        hr.MapPost("/data-changes/{id:guid}:withdraw", async (Guid id, DataChangeRequests requests, IDbSession session, CancellationToken ct) =>
        {
            var view = await requests.WithdrawAsync(id, ct);
            await session.CommitAsync(ct);
            return Results.Ok(view);
        });
        hr.MapPost("/data-changes/{id:guid}:complete", async (Guid id, DataChangeRequests requests, IDbSession session, CancellationToken ct) =>
        {
            var view = await requests.CompleteAsync(id, ct);
            await session.CommitAsync(ct);
            return Results.Ok(view);
        });
        return app;
    }
}

/// <summary>My HR details in the service catalogue (spec 008): one entry, shown with <c>hr.data_change</c>.</summary>
public sealed class DataChangeModuleServices : TeamBeats.Modules.Services.Contracts.IModuleServiceSource
{
    public const string Key = "hr.data_change";

    public IReadOnlyList<TeamBeats.Modules.Services.Contracts.ModuleServiceRegistration> Services { get; } =
    [
        new(Key, new Dictionary<string, string> { ["en"] = "My HR details", ["ar"] = "بياناتي لدى الموارد البشرية" },
            new Dictionary<string, string> { ["en"] = "Change your address, bank details and more", ["ar"] = "غيّر عنوانك وبياناتك المصرفية وغيرها" },
            "/my-details", null, Capabilities.HrDataChange),
    ];
}
