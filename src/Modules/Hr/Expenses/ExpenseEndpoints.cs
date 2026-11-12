using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using TeamBeats.Entitlements.Contracts;
using TeamBeats.Persistence;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.Hr.Expenses;

/// <summary>Expense endpoints (spec 022 slice 6). Every endpoint needs <c>hr.expenses</c> in the plan.</summary>
public static class ExpenseEndpoints
{
    private static string? IfMatch(HttpRequest http) => http.Headers.IfMatch.ToString() is { Length: > 0 } tag ? tag : null;

    public static IEndpointRouteBuilder MapExpenseEndpoints(this IEndpointRouteBuilder app)
    {
        var me = app.MapGroup("/api/v1/me/expenses").RequireTenantUser().RequiresCapability(Capabilities.HrExpenses).WithTags("Expenses");
        me.MapGet("", async (ExpenseClaims claims, CancellationToken ct) => Results.Ok(await claims.MineAsync(ct)));
        me.MapGet("/form", async (ExpenseClaims claims, CancellationToken ct) => Results.Ok(await claims.FormAsync(ct)));
        me.MapPost("", async (ExpenseClaimInput input, ExpenseClaims claims, IDbSession session, CancellationToken ct) =>
        {
            var view = await claims.CreateAsync(input, ct);
            await session.CommitAsync(ct);
            return Results.Created($"/api/v1/hr/expenses/{view.Id}", view);
        });
        me.MapPost("/receipts", async (string? name, HttpRequest http, ExpenseConfig config, IDbSession session, CancellationToken ct) =>
        {
            var buffer = new byte[ExpenseConfig.MaxBytes + 1];
            int read, total = 0;
            while ((read = await http.Body.ReadAsync(buffer.AsMemory(total, buffer.Length - total), ct)) > 0)
            {
                total += read;
                if (total > ExpenseConfig.MaxBytes)
                {
                    throw Primitives.AppException.Validation("file", "validation.expenses.receipt_size");
                }
            }
            var view = await config.UploadAsync(name, http.ContentType, buffer[..total], ct);
            await session.CommitAsync(ct);
            return Results.Created($"/api/v1/me/expenses/receipts/{view.Id}", view);
        }).DisableAntiforgery();
        // A suggestion only (FR-024): nothing is stored, so there is nothing to commit.
        me.MapPost("/receipts/{id:guid}:read", async (Guid id, ExpenseReceiptReader reader, CancellationToken ct) => Results.Ok(await reader.ReadAsync(id, ct)))
            .RequiresCapability(Capabilities.AiReceiptReading);

        var hr = app.MapGroup("/api/v1/hr").RequireTenantUser().RequiresCapability(Capabilities.HrExpenses).WithTags("Expenses");
        hr.MapGet("/expense-policy", async (ExpenseConfig config, HttpResponse response, CancellationToken ct) =>
        {
            var view = await config.PolicyViewAsync(ct);
            response.Headers.ETag = view.ETag;
            return Results.Ok(view);
        }).InAdminArea();
        hr.MapPut("/expense-policy", async (ExpensePolicyDocument policy, HttpRequest http, ExpenseConfig config, IDbSession session, CancellationToken ct) =>
        {
            var view = await config.UpdatePolicyAsync(IfMatch(http), policy, ct);
            await session.CommitAsync(ct);
            http.HttpContext.Response.Headers.ETag = view.ETag;
            return Results.Ok(view);
        }).InAdminArea();
        hr.MapGet("/expense-rates", async (ExpenseConfig config, CancellationToken ct) => Results.Ok(await config.RatesAsync(ct))).InAdminArea();
        hr.MapPut("/expense-rates", async (ExpenseRatesRequest request, ExpenseConfig config, IDbSession session, CancellationToken ct) =>
        {
            var rates = await config.PutRatesAsync(request, ct);
            await session.CommitAsync(ct);
            return Results.Ok(rates);
        }).InAdminArea();
        hr.MapGet("/expenses", async (string? status, ExpenseClaims claims, CancellationToken ct) => Results.Ok(await claims.ListAsync(status, ct))).InAdminArea();
        hr.MapGet("/expenses/{id:guid}", async (Guid id, ExpenseClaims claims, HttpResponse response, CancellationToken ct) =>
        {
            response.Headers.CacheControl = "no-store";
            return Results.Ok(await claims.GetAsync(id, ct));
        });
        hr.MapGet("/expenses/{id:guid}/receipts/{receiptId:guid}", async (Guid id, Guid receiptId, ExpenseClaims claims, HttpResponse response, CancellationToken ct) =>
        {
            var (content, type, name) = await claims.ReceiptAsync(id, receiptId, ct);
            response.Headers.CacheControl = "no-store";
            return Results.File(content, type, name);
        });
        hr.MapPost("/expenses/{id:guid}:withdraw", async (Guid id, ExpenseClaims claims, IDbSession session, CancellationToken ct) =>
        {
            var view = await claims.WithdrawAsync(id, ct);
            await session.CommitAsync(ct);
            return Results.Ok(view);
        });
        return app;
    }
}

/// <summary>Expense claims in the service catalogue (spec 008): one entry, shown with <c>hr.expenses</c>.</summary>
public sealed class ExpenseModuleServices : TeamBeats.Modules.Services.Contracts.IModuleServiceSource
{
    public const string Key = "hr.expenses";

    public IReadOnlyList<TeamBeats.Modules.Services.Contracts.ModuleServiceRegistration> Services { get; } =
    [
        new(Key, new Dictionary<string, string> { ["en"] = "Expense claims", ["ar"] = "مطالبات المصروفات" },
            new Dictionary<string, string> { ["en"] = "Claim back what you spent for work", ["ar"] = "استرد ما أنفقته على العمل" },
            "/expenses", null, Capabilities.HrExpenses),
    ];
}
