using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using TeamBeats.Entitlements.Contracts;
using TeamBeats.Persistence;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.Hr.Rosters;

/// <summary>Shift roster endpoints (spec 022 R3 slice 9). Every endpoint needs <c>hr.rosters</c> in the plan.</summary>
public static class RosterEndpoints
{
    public static IEndpointRouteBuilder MapRosterEndpoints(this IEndpointRouteBuilder app)
    {
        var hr = app.MapGroup("/api/v1/hr").RequireTenantUser().RequiresCapability(Capabilities.HrRosters).WithTags("Shift rosters");
        hr.MapGet("/shift-patterns", async (Rosters rosters, CancellationToken ct) => Results.Ok(await rosters.PatternsAsync(ct)));
        hr.MapPost("/shift-patterns", async (ShiftPatternInput input, Rosters rosters, IDbSession session, CancellationToken ct) =>
        {
            var view = await rosters.SavePatternAsync(null, input, ct);
            await session.CommitAsync(ct);
            return Results.Created($"/api/v1/hr/shift-patterns/{view.Id}", view);
        });
        hr.MapPut("/shift-patterns/{id:guid}", async (Guid id, ShiftPatternInput input, Rosters rosters, IDbSession session, CancellationToken ct) =>
        {
            var view = await rosters.SavePatternAsync(id, input, ct);
            await session.CommitAsync(ct);
            return Results.Ok(view);
        });
        hr.MapGet("/rosters/{weekStart}", async (DateOnly weekStart, Guid? managerId, Rosters rosters, CancellationToken ct) =>
            Results.Ok(await rosters.GetWeekAsync(weekStart, managerId, ct)));
        hr.MapPut("/rosters/{weekStart}", async (DateOnly weekStart, RosterSaveInput input, Rosters rosters, IDbSession session, CancellationToken ct) =>
        {
            var view = await rosters.SaveWeekAsync(weekStart, input, ct);
            await session.CommitAsync(ct);
            return Results.Ok(view);
        });
        hr.MapPost("/rosters/{weekStart}:publish", async (DateOnly weekStart, Rosters rosters, IDbSession session, CancellationToken ct) =>
        {
            var view = await rosters.PublishAsync(weekStart, ct);
            await session.CommitAsync(ct);
            return Results.Ok(view);
        });

        app.MapGroup("/api/v1/me/shifts").RequireTenantUser().RequiresCapability(Capabilities.HrRosters).WithTags("Shift rosters")
            .MapGet("", async (DateOnly? from, DateOnly? to, Rosters rosters, CancellationToken ct) => Results.Ok(await rosters.MyShiftsAsync(from, to, ct)));
        return app;
    }
}
