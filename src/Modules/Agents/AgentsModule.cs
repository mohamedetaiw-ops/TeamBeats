using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using TeamBeats.Messaging;
using TeamBeats.Persistence;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.Agents;

/// <summary>
/// Agents (ADR-0011, module map: Agents). In R1 it holds the assistant of spec 014: the knowledge agent, conversations,
/// feedback and retention. Agent definitions, routing and execution records of specs 020 and 027 join it later.
/// </summary>
public static class AgentsModule
{
    public static readonly MigrationSource Migrations = new("agents", typeof(AgentsModule).Assembly);

    public static IServiceCollection AddAgentsModule(this IServiceCollection services)
    {
        services.AddScoped<AssistantService>();
        services.AddScoped<AssistantHistory>();
        services.AddScoped<AssistantTextActions>();
        services.AddScoped<Contracts.IAgentTool, KnowledgeSearchTool>();
        services.AddScoped<ToolRegistry>();
        services.AddScoped<AgentPolicyService>();
        services.AddScoped<AgentRegistry>();
        services.AddScoped<AgentOwnerHandover>();
        services.AddScoped<AgentAnalytics>();
        services.AddMessageHandler<Identity.Contracts.UserDeprovisioned, AgentOwnerHandler>("agents.owner_left");
        services.AddScoped<Contracts.IAgentCatalog, AgentCatalog>();
        services.AddScoped<Contracts.IAgentEvaluationRuntime, AgentEvaluationRuntime>();
        services.AddScoped<Contracts.IAssistantEvaluationRuntime, AssistantEvaluationRuntime>();
        services.AddScoped<AgentRuns>();
        services.AddScoped<AgentLifecycle>();
        services.AddScoped<AgentRouter>();
        services.TryAddScoped<Contracts.IAgentReleaseGate, NoReleaseGate>();
        services.AddScoped<Workflow.Contracts.IPersonalTaskSource, AgentReviewTasks>();
        return services;
    }

    private static bool WantsStream(HttpRequest request) =>
        request.Headers.Accept.Any(a => a is not null && a.Contains("text/event-stream", StringComparison.OrdinalIgnoreCase));

    private static async Task WriteEventAsync(HttpResponse response, string name, object data, CancellationToken ct)
    {
        await response.WriteAsync($"event: {name}\ndata: {JsonSerializer.Serialize(data, AssistantService.Json)}\n\n", ct);
        await response.Body.FlushAsync(ct);
    }

    public static IEndpointRouteBuilder MapAgentsEndpoints(this IEndpointRouteBuilder app)
    {
        var root = app.MapGroup("/api/v1/assistant").RequireTenantUser().WithTags("Assistant");

        // A turn (US1, FR-002 to FR-004). With Accept: text/event-stream the answer streams as server-sent events:
        // "delta" events with text as it arrives, then "done" with the stored turn and its checked citations.
        root.MapPost("/turns", async (TurnRequest request, HttpContext http, AssistantService assistant, IDbSession session, ILoggerFactory logs,
            CancellationToken ct) =>
        {
            if (!WantsStream(http.Request))
            {
                var view = await assistant.AskAsync(request, ct);
                await session.CommitAsync(ct);
                return Results.Ok(view);
            }

            var events = await assistant.StreamAsync(request, ct);
            var response = http.Response;
            response.ContentType = "text/event-stream";
            response.Headers.CacheControl = "no-cache, no-transform";
            response.Headers["X-Accel-Buffering"] = "no";
            http.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();
            try
            {
                await foreach (var e in events.WithCancellation(ct))
                {
                    if (e.Done is { } done)
                    {
                        await session.CommitAsync(ct);
                        await WriteEventAsync(response, "done", done, ct);
                    }
                    else
                    {
                        await WriteEventAsync(response, "delta", new { text = e.Delta }, ct);
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Headers are sent, so a problem response is no longer possible; the client shows its failure message.
                logs.CreateLogger("TeamBeats.Assistant").LogError(ex, "Assistant turn failed while streaming");
                await WriteEventAsync(response, "error", new { code = "failed" }, CancellationToken.None);
            }
            return Results.Empty;
        });

        // Summarize, explain, translate, rewrite and draft (US3, FR-007). Nothing is stored, published or sent.
        root.MapPost("/actions", async (TextActionRequest request, AssistantTextActions actions, IDbSession session, CancellationToken ct) =>
        {
            var result = await actions.RunAsync(request, ct);
            await session.CommitAsync(ct);
            return Results.Ok(result);
        });

        root.MapPost("/turns/{id:guid}/feedback", async (Guid id, FeedbackRequest request, AssistantHistory history, IDbSession session, CancellationToken ct) =>
        {
            await history.GiveFeedbackAsync(id, request, ct);
            await session.CommitAsync(ct);
            return Results.NoContent();
        });

        root.MapGet("/conversations", async (AssistantHistory history, CancellationToken ct) =>
            Results.Ok(new { conversations = await history.ListAsync(ct) }));

        root.MapGet("/conversations/{id:guid}", async (Guid id, AssistantHistory history, CancellationToken ct) => Results.Ok(await history.GetAsync(id, ct)));

        root.MapDelete("/conversations/{id:guid}", async (Guid id, AssistantHistory history, IDbSession session, CancellationToken ct) =>
        {
            await history.DeleteAsync(id, ct);
            await session.CommitAsync(ct);
            return Results.NoContent();
        });

        root.MapDelete("/conversations", async (AssistantHistory history, IDbSession session, CancellationToken ct) =>
        {
            var removed = await history.DeleteAllAsync(ct);
            await session.CommitAsync(ct);
            return Results.Ok(new { removed });
        });

        root.MapGet("/settings", async (AssistantHistory history, CancellationToken ct) => Results.Ok(await history.SettingsAsync(ct)));

        root.MapPut("/settings", async (AssistantSettingsRequest request, AssistantHistory history, IDbSession session, CancellationToken ct) =>
        {
            var view = await history.ChangeSettingsAsync(request, ct);
            await session.CommitAsync(ct);
            return Results.Ok(view);
        });

        // The agent registry, templates, tools and tenant agent policy (specs 020 and 027, R1 foundations).
        var agents = app.MapGroup("/api/v1/agents").RequireTenantUser().WithTags("Agents");

        agents.MapGet("/templates", async (AgentRegistry registry, CancellationToken ct) => Results.Ok(new { templates = await registry.TemplatesAsync(ct) }));

        agents.MapGet("/tools", async (AgentRegistry registry, CancellationToken ct) => Results.Ok(new { tools = await registry.ToolsAsync(ct) }));

        agents.MapPost("", async (InstallRequest request, AgentRegistry registry, IDbSession session, CancellationToken ct) =>
        {
            var detail = await registry.InstallAsync(request, ct);
            await session.CommitAsync(ct);
            return Results.Created($"/api/v1/agents/{detail.Agent.Id}", detail);
        });

        // Routing (020 T012, 014 T004): the agents the person may use, and a routing decision for a message.
        agents.MapGet("/available", async (AgentRouter router, CancellationToken ct) => Results.Ok(new { agents = await router.AvailableAsync(ct) }));

        agents.MapPost("/route", async (RouteRequest request, AgentRouter router, IDbSession session, CancellationToken ct) =>
        {
            var view = await router.RouteAsync(request, ct);
            await session.CommitAsync(ct);
            return Results.Ok(view);
        });

        agents.MapGet("", async (AgentRegistry registry, CancellationToken ct) => Results.Ok(new { agents = await registry.ListAsync(ct) }));

        agents.MapGet("/{id:guid}", async (Guid id, AgentRegistry registry, CancellationToken ct) => Results.Ok(await registry.GetAsync(id, ct)));

        agents.MapPut("/{id:guid}/draft", async (Guid id, DraftRequest request, AgentRegistry registry, IDbSession session, CancellationToken ct) =>
        {
            var detail = await registry.SaveDraftAsync(id, request, ct);
            await session.CommitAsync(ct);
            return Results.Ok(detail);
        });

        agents.MapGet("/{id:guid}/analytics", async (Guid id, int? days, AgentAnalytics analytics, CancellationToken ct) =>
            Results.Ok(await analytics.ForAgentAsync(id, days, ct)));

        agents.MapPost("/{id:guid}/template-update", async (Guid id, TemplateUpdateRequest request, AgentRegistry registry, IDbSession session, CancellationToken ct) =>
        {
            var detail = await registry.ApplyTemplateUpdateAsync(id, request, ct);
            await session.CommitAsync(ct);
            return Results.Ok(detail);
        });

        agents.MapPut("/{id:guid}/owners", async (Guid id, OwnersRequest request, AgentRegistry registry, IDbSession session, CancellationToken ct) =>
        {
            var detail = await registry.SetOwnersAsync(id, request, ct);
            await session.CommitAsync(ct);
            return Results.Ok(detail);
        });

        // Runs (020 T005, 027 T005, T006, T010): a turn with a published agent, the designer's test panel, confirmation
        // cards, stopping a run, and the execution records.
        agents.MapPost("/{id:guid}/runs", async (Guid id, RunRequest request, AgentRuns runs, IDbSession session, CancellationToken ct) =>
        {
            var view = await runs.RunAsync(id, request, ct);
            await session.CommitAsync(ct);
            return Results.Ok(view);
        });

        agents.MapPost("/{id:guid}/test", async (Guid id, RunRequest request, AgentRuns runs, IDbSession session, CancellationToken ct) =>
        {
            var view = await runs.TestAsync(id, request, ct);
            await session.CommitAsync(ct);
            return Results.Ok(view);
        });

        agents.MapPost("/runs/{id:guid}/confirmations/{confirmation:guid}", async (Guid id, Guid confirmation, ConfirmRequest request, AgentRuns runs, IDbSession session,
            CancellationToken ct) =>
        {
            var view = await runs.ConfirmAsync(id, confirmation, request, ct);
            await session.CommitAsync(ct);
            return Results.Ok(view);
        });

        agents.MapPost("/runs/{id:guid}/stop", async (Guid id, AgentRuns runs, IDbSession session, CancellationToken ct) =>
        {
            var view = await runs.StopAsync(id, ct);
            await session.CommitAsync(ct);
            return Results.Ok(view);
        });

        agents.MapGet("/runs", async (Guid? agent, Guid? user, string? tool, string? outcome, int? limit, AgentRuns runs, CancellationToken ct) =>
            Results.Ok(new { runs = await runs.SearchAsync(agent, user, tool, outcome, limit ?? 50, ct) }));

        agents.MapGet("/runs/{id:guid}", async (Guid id, AgentRuns runs, CancellationToken ct) => Results.Ok(await runs.RecordAsync(id, ct)));

        // The lifecycle (020 T008 to T011, 027 T010, T011): submit, review, publish with separation of duties, reject,
        // rollback, suspend, resume, retire and the tenant kill switch.
        agents.MapPost("/{id:guid}/submit", async (Guid id, SubmitRequest request, AgentLifecycle lifecycle, IDbSession session, CancellationToken ct) =>
        {
            var detail = await lifecycle.SubmitAsync(id, request, ct);
            await session.CommitAsync(ct);
            return Results.Ok(detail);
        });

        agents.MapGet("/{id:guid}/review", async (Guid id, AgentLifecycle lifecycle, CancellationToken ct) => Results.Ok(await lifecycle.ReviewAsync(id, ct)));

        agents.MapPost("/{id:guid}/publish", async (Guid id, DecisionRequest request, AgentLifecycle lifecycle, IDbSession session, CancellationToken ct) =>
        {
            var detail = await lifecycle.PublishAsync(id, request, ct);
            await session.CommitAsync(ct);
            return Results.Ok(detail);
        });

        agents.MapPost("/{id:guid}/reject", async (Guid id, DecisionRequest request, AgentLifecycle lifecycle, IDbSession session, CancellationToken ct) =>
        {
            var detail = await lifecycle.RejectAsync(id, request, ct);
            await session.CommitAsync(ct);
            return Results.Ok(detail);
        });

        agents.MapPost("/{id:guid}/rollback", async (Guid id, RollbackRequest request, AgentLifecycle lifecycle, IDbSession session, CancellationToken ct) =>
        {
            var detail = await lifecycle.RollbackAsync(id, request, ct);
            await session.CommitAsync(ct);
            return Results.Ok(detail);
        });

        agents.MapPost("/{id:guid}/suspend", async (Guid id, ReasonRequest request, AgentLifecycle lifecycle, IDbSession session, CancellationToken ct) =>
        {
            var detail = await lifecycle.SuspendAsync(id, request, ct);
            await session.CommitAsync(ct);
            return Results.Ok(detail);
        });

        agents.MapPost("/{id:guid}/resume", async (Guid id, AgentLifecycle lifecycle, IDbSession session, CancellationToken ct) =>
        {
            var detail = await lifecycle.ResumeAsync(id, ct);
            await session.CommitAsync(ct);
            return Results.Ok(detail);
        });

        agents.MapPost("/{id:guid}/retire", async (Guid id, ReasonRequest request, AgentLifecycle lifecycle, IDbSession session, CancellationToken ct) =>
        {
            var detail = await lifecycle.RetireAsync(id, request, ct);
            await session.CommitAsync(ct);
            return Results.Ok(detail);
        });

        agents.MapGet("/kill-switch", async (AgentLifecycle lifecycle, CancellationToken ct) => Results.Ok(await lifecycle.KillSwitchAsync(ct)));

        agents.MapPut("/kill-switch", async (KillSwitchRequest request, AgentLifecycle lifecycle, IDbSession session, CancellationToken ct) =>
        {
            var view = await lifecycle.ChangeKillSwitchAsync(request, ct);
            await session.CommitAsync(ct);
            return Results.Ok(view);
        });

        agents.MapGet("/policy", async (AgentPolicyService policies, CancellationToken ct) => Results.Ok(await policies.GetAsync(ct)));

        agents.MapPut("/policy", async (AgentPolicyRequest request, AgentPolicyService policies, AgentLifecycle lifecycle, IDbSession session, CancellationToken ct) =>
        {
            var view = await policies.ChangeAsync(request, ct);
            // Published agents that break the new policy are suspended in the same transaction (027 FR-018).
            await lifecycle.SuspendBreakingAsync(view.Policy, ct);
            await session.CommitAsync(ct);
            return Results.Ok(view);
        });

        return app;
    }
}
