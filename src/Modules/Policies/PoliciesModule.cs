using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using TeamBeats.Messaging;
using TeamBeats.Persistence;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.Policies;

/// <summary>Policy management (spec 025, module map: Policies).</summary>
public static class PoliciesModule
{
    public static readonly MigrationSource Migrations = new("policies", typeof(PoliciesModule).Assembly);

    public static IServiceCollection AddPoliciesModule(this IServiceCollection services)
    {
        services.AddScoped<TeamBeats.Modules.Search.Contracts.ISearchSource, PolicySearchSource>();
        services.AddScoped<TeamBeats.Modules.Analytics.Contracts.IAnalyticsItemSource, PolicyAnalyticsSource>();
        services.AddScoped<PolicyRegister>();
        services.AddScoped<PolicyApprovals>();
        services.AddScoped<PolicyTaskSource>();
        services.AddScoped<Workflow.Contracts.ITaskSource>(sp => sp.GetRequiredService<PolicyTaskSource>());
        services.AddMessageHandler<Workflow.Contracts.UserTaskCreated, PolicyTaskSource>("policies.notify_approval");
        services.AddScoped<PolicyCampaigns>();
        services.AddScoped<Content.Contracts.IOfflineSource, PolicyOfflineSource>();
        services.AddScoped<PolicyCompliance>();
        services.AddScoped<Contracts.IPolicyObligations, PolicyObligations>();
        services.AddScoped<Workflow.Contracts.IPersonalTaskSource>(sp => sp.GetRequiredService<PolicyCampaigns>());
        services.AddMessageHandler<AttestationNoticeBatch, PolicyCampaigns>("policies.attestation_notice");
        services.AddMessageHandler<Tenancy.Contracts.OrganizationMembershipChanged, PolicyCampaigns>("policies.campaign_membership");
        services.AddMessageHandler<Tenancy.Contracts.TenantMembershipDisabled, PolicyCampaigns>("policies.campaign_disabled");
        services.AddMessageHandler<Contracts.PolicyPublished, PolicyCampaigns>("policies.campaign_outdated");
        services.AddMessageHandler<Contracts.PolicyPublished, LinkedTraining>("policies.linked_training");
        return services;
    }

    public static IEndpointRouteBuilder MapPoliciesEndpoints(this IEndpointRouteBuilder app)
    {
        var root = app.MapGroup("/api/v1/policies").RequireTenantUser().WithTags("Policies");

        root.MapGet("/rights", async (PolicyRegister register, CancellationToken ct) => Results.Ok(await register.RightsAsync(ct)));

        root.MapGet("/categories", async (PolicyRegister register, CancellationToken ct) => Results.Ok(await register.CategoriesAsync(ct)));

        root.MapPost("/categories", async (CategoryRequest request, PolicyRegister register, IDbSession session, CancellationToken ct) =>
        {
            var view = await register.CreateCategoryAsync(request, ct);
            await session.CommitAsync(ct);
            return Results.Created($"/api/v1/policies/categories/{view.Id}", view);
        });

        root.MapGet("", async (Guid? category, string? status, string? q, PolicyRegister register, CancellationToken ct) =>
            Results.Ok(await register.ListAsync(category, status, q, ct)));

        root.MapPost("", async (PolicyRequest request, PolicyRegister register, IDbSession session, CancellationToken ct) =>
        {
            var view = await register.CreateAsync(request, ct);
            await session.CommitAsync(ct);
            return Results.Created($"/api/v1/policies/{view.Id}", view);
        });

        root.MapGet("/{id:guid}", async (Guid id, PolicyRegister register, CancellationToken ct) => Results.Ok(await register.GetAsync(id, ct)));

        root.MapPut("/{id:guid}", async (Guid id, PolicyRequest request, PolicyRegister register, IDbSession session, CancellationToken ct) =>
        {
            var view = await register.UpdateAsync(id, request, ct);
            await session.CommitAsync(ct);
            return Results.Ok(view);
        });

        root.MapPut("/{id:guid}/training", async (Guid id, TrainingRequest request, PolicyRegister register, Learning.Contracts.ILearningObligations training,
            IDbSession session, CancellationToken ct) =>
        {
            var view = await register.LinkTrainingAsync(id, request, training, ct);
            await session.CommitAsync(ct);
            return Results.Ok(view);
        });

        root.MapPost("/{id:guid}/retire", async (Guid id, PolicyRegister register, IDbSession session, CancellationToken ct) =>
        {
            var view = await register.RetireAsync(id, ct);
            await session.CommitAsync(ct);
            return Results.Ok(view);
        });

        root.MapGet("/{id:guid}/current", async (Guid id, PolicyRegister register, CancellationToken ct) => Results.Ok(await register.CurrentAsync(id, ct)));

        root.MapGet("/{id:guid}/versions", async (Guid id, PolicyRegister register, CancellationToken ct) => Results.Ok(await register.HistoryAsync(id, ct)));

        root.MapGet("/{id:guid}/versions/{number:int}", async (Guid id, int number, PolicyRegister register, CancellationToken ct) =>
            Results.Ok(await register.NumberedAsync(id, number, ct)));

        root.MapGet("/{id:guid}/draft", async (Guid id, PolicyApprovals approvals, CancellationToken ct) => Results.Ok(await approvals.DraftAsync(id, ct)));

        root.MapPut("/{id:guid}/draft", async (Guid id, DraftRequest request, PolicyApprovals approvals, IDbSession session, CancellationToken ct) =>
        {
            var view = await approvals.SaveDraftAsync(id, request, ct);
            await session.CommitAsync(ct);
            return Results.Ok(view);
        });

        root.MapPost("/{id:guid}/submit", async (Guid id, SubmitRequest request, PolicyApprovals approvals, IDbSession session, CancellationToken ct) =>
        {
            var view = await approvals.SubmitAsync(id, request, ct);
            await session.CommitAsync(ct);
            return Results.Ok(view);
        });

        root.MapPost("/{id:guid}/withdraw", async (Guid id, PolicyApprovals approvals, IDbSession session, CancellationToken ct) =>
        {
            var view = await approvals.WithdrawAsync(id, ct);
            await session.CommitAsync(ct);
            return Results.Ok(view);
        });

        root.MapGet("/{id:guid}/compare", async (Guid id, PolicyApprovals approvals, CancellationToken ct) => Results.Ok(await approvals.CompareAsync(id, ct)));

        root.MapGet("/{id:guid}/drafts/{versionId:guid}/comments", async (Guid id, Guid versionId, PolicyApprovals approvals, CancellationToken ct) =>
            Results.Ok(await approvals.CommentsAsync(id, versionId, ct)));

        root.MapPost("/{id:guid}/drafts/{versionId:guid}/comments", async (Guid id, Guid versionId, CommentRequest request, PolicyApprovals approvals, IDbSession session,
            CancellationToken ct) =>
        {
            var view = await approvals.AddCommentAsync(id, versionId, request, ct);
            await session.CommitAsync(ct);
            return Results.Created($"/api/v1/policies/{id}/drafts/{versionId}/comments/{view.Id}", view);
        });

        root.MapPost("/{id:guid}/drafts/{versionId:guid}/comments/{commentId:guid}/resolve", async (Guid id, Guid versionId, Guid commentId, PolicyApprovals approvals,
            IDbSession session, CancellationToken ct) =>
        {
            var view = await approvals.ResolveCommentAsync(id, versionId, commentId, ct);
            await session.CommitAsync(ct);
            return Results.Ok(view);
        });

        root.MapGet("/{id:guid}/campaigns", async (Guid id, PolicyCampaigns campaigns, CancellationToken ct) => Results.Ok(await campaigns.ListAsync(id, ct)));

        root.MapPost("/{id:guid}/campaigns", async (Guid id, CampaignRequest request, PolicyCampaigns campaigns, IDbSession session, CancellationToken ct) =>
        {
            var view = await campaigns.LaunchAsync(id, request, ct);
            await session.CommitAsync(ct);
            return Results.Created($"/api/v1/policies/{id}/campaigns/{view.Id}", view);
        });

        root.MapGet("/{id:guid}/campaigns/{campaignId:guid}", async (Guid id, Guid campaignId, PolicyCampaigns campaigns, CancellationToken ct) =>
            Results.Ok(await campaigns.GetAsync(id, campaignId, ct)));

        root.MapPost("/{id:guid}/campaigns/{campaignId:guid}/remind", async (Guid id, Guid campaignId, PolicyCampaigns campaigns, IDbSession session, CancellationToken ct) =>
        {
            var view = await campaigns.RemindAsync(id, campaignId, ct);
            await session.CommitAsync(ct);
            return Results.Ok(view);
        });

        root.MapPost("/{id:guid}/campaigns/{campaignId:guid}/close", async (Guid id, Guid campaignId, PolicyCampaigns campaigns, IDbSession session, CancellationToken ct) =>
        {
            var view = await campaigns.CloseAsync(id, campaignId, ct);
            await session.CommitAsync(ct);
            return Results.Ok(view);
        });

        root.MapGet("/{id:guid}/attestation", async (Guid id, PolicyCampaigns campaigns, CancellationToken ct) => Results.Ok(await campaigns.MineAsync(id, ct)));

        root.MapPost("/{id:guid}/attest", async (Guid id, AttestRequest request, PolicyCampaigns campaigns, IDbSession session, CancellationToken ct) =>
        {
            var view = await campaigns.AttestAsync(id, request, ct);
            await session.CommitAsync(ct);
            return Results.Created($"/api/v1/policies/{id}/attestations/{view.Id}", view);
        });

        root.MapGet("/{id:guid}/compliance", async (Guid id, Guid? campaign, Guid? unit, PolicyCompliance compliance, CancellationToken ct) =>
            Results.Ok(await compliance.ViewAsync(id, campaign, unit, ct)));

        root.MapGet("/{id:guid}/compliance/people", async (Guid id, Guid? campaign, Guid? unit, bool? direct, string? location, string? manager, string? status, int? offset,
            int? limit, PolicyCompliance compliance, CancellationToken ct) =>
            Results.Ok(await compliance.PeopleAsync(id, campaign, unit, direct ?? false, location, manager, status, offset, limit, ct)));

        root.MapPost("/{id:guid}/compliance/remind", async (Guid id, ComplianceRemindRequest request, PolicyCompliance compliance, IDbSession session, CancellationToken ct) =>
        {
            var result = await compliance.RemindAsync(id, request, ct);
            await session.CommitAsync(ct);
            return Results.Ok(result);
        });

        root.MapGet("/compliance/people/{userId:guid}", async (Guid userId, PolicyCompliance compliance, CancellationToken ct) =>
            Results.Ok(await compliance.PersonAsync(userId, ct)));

        app.MapGet("/api/v1/me/policies", async (PolicyCampaigns campaigns, CancellationToken ct) => Results.Ok(await campaigns.MyPoliciesAsync(ct)))
            .RequireTenantUser().WithTags("Policies");

        app.MapPolicyPublicEndpoints();
        return app;
    }
}
