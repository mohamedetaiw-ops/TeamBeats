using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using TeamBeats.Entitlements.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Primitives;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.Hr.Letters;

/// <summary>Letter endpoints (spec 022 slice 5). Every endpoint needs <c>hr.letters</c> in the plan.</summary>
public static class LetterEndpoints
{
    /// <summary>The public verification page is a guessing path: limit per client address.</summary>
    public const string VerifyRateLimit = "letters.verify";

    private static string? IfMatch(HttpRequest http) => http.Headers.IfMatch.ToString() is { Length: > 0 } tag ? tag : null;

    public static IEndpointRouteBuilder MapLetterEndpoints(this IEndpointRouteBuilder app)
    {
        var me = app.MapGroup("/api/v1/me/letters").RequireTenantUser().RequiresCapability(Capabilities.HrLetters).WithTags("Letters");
        me.MapGet("", async (LetterRequests letters, CancellationToken ct) => Results.Ok(await letters.MineAsync(ct)));
        me.MapGet("/types", async (LetterConfig config, CancellationToken ct) => Results.Ok(await config.ChoicesAsync(ct)));
        me.MapPost("", async (LetterRequestInput input, LetterRequests letters, IDbSession session, CancellationToken ct) =>
        {
            var view = await letters.CreateAsync(input, ct);
            await session.CommitAsync(ct);
            return Results.Created($"/api/v1/hr/letters/{view.Id}", view);
        });

        var hr = app.MapGroup("/api/v1/hr").RequireTenantUser().RequiresCapability(Capabilities.HrLetters).WithTags("Letters");

        hr.MapGet("/letter-types", async (LetterConfig config, CancellationToken ct) => Results.Ok(await config.ListTypesAsync(ct))).InAdminArea();

        hr.MapPut("/letter-types/{key}", async (string key, LetterTypeRequest request, HttpRequest http, LetterConfig config, IDbSession session, CancellationToken ct) =>
        {
            var view = await config.PutTypeAsync(key, IfMatch(http), request, ct);
            await session.CommitAsync(ct);
            http.HttpContext.Response.Headers.ETag = view.ETag;
            return Results.Ok(view);
        }).InAdminArea();

        hr.MapGet("/letter-settings", async (LetterConfig config, HttpResponse response, CancellationToken ct) =>
        {
            var view = await config.SettingsAsync(ct);
            response.Headers.ETag = view.ETag;
            return Results.Ok(view);
        }).InAdminArea();

        hr.MapPut("/letter-settings", async (LetterSettingsRequest request, HttpRequest http, LetterConfig config, IDbSession session, CancellationToken ct) =>
        {
            var view = await config.UpdateSettingsAsync(IfMatch(http), request, ct);
            await session.CommitAsync(ct);
            http.HttpContext.Response.Headers.ETag = view.ETag;
            return Results.Ok(view);
        }).InAdminArea();

        hr.MapGet("/letters", async (string? status, LetterRequests letters, CancellationToken ct) => Results.Ok(await letters.ListAsync(status, ct))).InAdminArea();

        hr.MapGet("/letters/{id:guid}", async (Guid id, LetterRequests letters, CancellationToken ct) => Results.Ok(await letters.GetAsync(id, ct)));

        hr.MapPut("/letters/{id:guid}/values", async (Guid id, LetterValuesRequest request, LetterRequests letters, IDbSession session, CancellationToken ct) =>
        {
            var view = await letters.SetValuesAsync(id, request, ct);
            await session.CommitAsync(ct);
            return Results.Ok(view);
        });

        hr.MapPost("/letters/{id:guid}:withdraw", async (Guid id, LetterRequests letters, IDbSession session, CancellationToken ct) =>
        {
            var view = await letters.WithdrawAsync(id, ct);
            await session.CommitAsync(ct);
            return Results.Ok(view);
        });

        hr.MapGet("/letters/{id:guid}/pdf", async (Guid id, LetterRequests letters, HttpResponse response, CancellationToken ct) =>
        {
            var (content, name) = await letters.PdfAsync(id, ct);
            response.Headers.CacheControl = "no-store";
            return Results.File(content, "application/pdf", name);
        });

        // The verification page (US7 scenario 2). No session: whoever holds the letter checks it. The tenant comes from
        // the host, so a code of another tenant is not found; the code is the only thing that opens a letter.
        app.MapGet("/api/v1/letters/verify/{code}", async (string code, ITenantContext context, TenantContextAccessor accessor, LetterRequests letters,
            IDbSession session, HttpResponse response, CancellationToken ct) =>
        {
            if (context.Host.Kind != HostScopeKind.Tenant || context.Host.Tenant is not { Status: TenantLifecycleStatus.Active } tenant)
            {
                throw AppException.NotFound();
            }
            accessor.SetTenant(tenant.Id);
            accessor.SetActor(ActorType.Anonymous, null);
            var view = await letters.VerifyAsync(code, ct);
            await session.CommitAsync(ct);
            response.Headers.CacheControl = "no-store";
            return Results.Ok(view);
        }).RequireRateLimiting(VerifyRateLimit).WithTags("Letters");

        return app;
    }
}

/// <summary>Letters in the service catalogue (spec 008): one entry, shown with <c>hr.letters</c>.</summary>
public sealed class LetterModuleServices : TeamBeats.Modules.Services.Contracts.IModuleServiceSource
{
    public const string Key = "hr.letters";

    public IReadOnlyList<TeamBeats.Modules.Services.Contracts.ModuleServiceRegistration> Services { get; } =
    [
        new(Key, new Dictionary<string, string> { ["en"] = "HR letters", ["ar"] = "الخطابات والشهادات" },
            new Dictionary<string, string> { ["en"] = "Salary certificates, experience letters and more", ["ar"] = "شهادات الراتب وخطابات الخبرة وغيرها" },
            "/letters", null, Capabilities.HrLetters, Icon: "files"),
    ];
}
