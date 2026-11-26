using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using TeamBeats.Entitlements.Contracts;
using TeamBeats.Persistence;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.Hr.Milestones;

/// <summary>Probation and contract milestone endpoints (spec 022 R3 slice 8). Every endpoint needs <c>hr.milestones</c> in the plan.</summary>
public static class MilestoneEndpoints
{
    public static IEndpointRouteBuilder MapMilestoneEndpoints(this IEndpointRouteBuilder app)
    {
        var hr = app.MapGroup("/api/v1/hr/milestones").RequireTenantUser().RequiresCapability(Capabilities.HrMilestones).WithTags("Milestones");
        hr.MapGet("", async (string? kind, string? status, Milestones milestones, CancellationToken ct) => Results.Ok(await milestones.ListAsync(kind, status, ct)));
        hr.MapGet("/{id:guid}", async (Guid id, Milestones milestones, CancellationToken ct) => Results.Ok(await milestones.GetAsync(id, ct)));
        hr.MapPut("/people/{userId:guid}/{kind}", async (Guid userId, string kind, MilestoneDateInput input, Milestones milestones, IDbSession session,
            CancellationToken ct) =>
        {
            var view = await milestones.SetDateAsync(userId, kind, input, ct);
            await session.CommitAsync(ct);
            return view is null ? Results.NoContent() : Results.Ok(view);
        });
        hr.MapPost("/{id:guid}/review", async (Guid id, MilestoneReviewInput input, Milestones milestones, IDbSession session, CancellationToken ct) =>
        {
            var view = await milestones.ReviewAsync(id, input, ct);
            await session.CommitAsync(ct);
            return Results.Ok(view);
        });
        return app;
    }
}
