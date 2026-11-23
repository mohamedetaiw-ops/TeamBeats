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
        services.AddSingleton<TeamBeats.Modules.Experience.Contracts.IShellCommands, AssistantCommands>();
        services.AddScoped<AssistantGuests>();
        services.AddScoped<AssistantFiles>();
        services.AddScoped<AssistantVoice>();
        services.AddScoped<AgentMarketplace>();
        services.AddScoped<AgentMemory>();
        services.AddScoped<MemoryRememberTool>();
        services.AddScoped<AgentBriefings>();
        services.AddScoped<AgentBriefingRunner>();
        services.AddScoped<AgentRunResumer>();
        services.AddScoped<Workflow.Contracts.IPersonalTaskSource, AgentAccessTasks>();
        services.AddScoped<TeamBeats.Modules.Agents.Contracts.ISearchSummaries, SearchSummaries>();
        services.AddScoped<AssistantHistory>();
        services.AddScoped<AssistantTextActions>();
        services.AddScoped<Contracts.IAgentTool, KnowledgeSearchTool>();
        services.AddScoped<Contracts.IAgentTool, AskTeamBeatsTool>();
        services.AddScoped<ExternalToolGateway>();
        services.AddScoped<Contracts.IExternalToolGateway>(sp => sp.GetRequiredService<ExternalToolGateway>());
        services.AddScoped<ExternalActions>();
        services.AddScoped<Workflow.Contracts.IPersonalTaskSource, ExternalActionTasks>();
        services.AddScoped<ToolRegistry>();
        services.AddScoped<AgentPolicyService>();
        services.AddScoped<UnitAgentPolicyService>();
        services.AddScoped<AgentSecurityCenter>();
        services.AddScoped<AgentRegistry>();
        services.AddScoped<AgentOwnerHandover>();
        services.AddScoped<AgentAnalytics>();
        services.AddScoped<Contracts.IAssistantUsageFigures, AssistantUsageFigures>();
        services.AddScoped<Contracts.IAssistantKnowledgeFigures, AssistantKnowledgeFigures>();
        services.AddMessageHandler<Identity.Contracts.UserDeprovisioned, AgentOwnerHandler>("agents.owner_left");
        services.AddMessageHandler<Identity.Contracts.UserDeprovisioned, AgentMemoryLeavers>("agents.memory_leaver");
        services.AddMessageHandler<Identity.Contracts.UserDeprovisioned, AgentBriefingLeavers>("agents.briefing_leaver");
        services.AddScoped<Contracts.IAgentCatalog, AgentCatalog>();
        services.AddScoped<Contracts.IAgentEvaluationRuntime, AgentEvaluationRuntime>();
        services.AddScoped<Contracts.IAssistantEvaluationRuntime, AssistantEvaluationRuntime>();
        services.AddScoped<AgentRuns>();
        services.AddScoped<AgentApprovals>();
        services.AddScoped<Workflow.Contracts.ITaskSource>(sp => sp.GetRequiredService<AgentApprovals>());
        services.AddScoped<AgentLifecycle>();
        services.AddScoped<AgentRouter>();
        services.TryAddScoped<Contracts.IAgentReleaseGate, NoReleaseGate>();
        services.TryAddScoped<Contracts.IProductionSampler, NoProductionSampler>();
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

    /// <summary>Reads a request body up to <paramref name="max"/> bytes; one byte more is kept so the caller sees it was too large.</summary>
    private static async Task<byte[]> ReadCappedAsync(Stream body, long max, CancellationToken ct)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await body.ReadAsync(chunk, ct)) > 0)
        {
            buffer.Write(chunk, 0, read);
            if (buffer.Length > max)
            {
                break;
            }
        }
        return buffer.ToArray();
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

        // Files the person brings to a conversation (US5, AST-015). The raw file is the body; only its text is kept.
        root.MapPost("/files", async (HttpRequest http, string? name, string? classification, Guid? conversationId, AssistantFiles files, IDbSession session,
            CancellationToken ct) =>
        {
            var data = await ReadCappedAsync(http.Body, AssistantFiles.MaxBytes, ct);
            var view = await files.UploadAsync(conversationId, name, classification, data, ct);
            await session.CommitAsync(ct);
            return Results.Created($"/api/v1/assistant/files/{view.Id}", view);
        }).DisableAntiforgery();

        root.MapGet("/files", async (Guid? conversationId, AssistantFiles files, CancellationToken ct) =>
            Results.Ok(new { files = await files.ListAsync(conversationId, ct) }));

        root.MapDelete("/files/{id:guid}", async (Guid id, AssistantFiles files, IDbSession session, CancellationToken ct) =>
        {
            await files.DeleteAsync(id, ct);
            await session.CommitAsync(ct);
            return Results.NoContent();
        });

        // Voice (spec 014 US6): the audio of a request is read into memory, sent through the gateway and dropped.
        root.MapGet("/voice", async (AssistantVoice voice, CancellationToken ct) => Results.Ok(await voice.OfferAsync(ct)));

        root.MapPost("/transcribe", async (HttpRequest http, string? language, AssistantVoice voice, CancellationToken ct) =>
        {
            var audio = await ReadCappedAsync(http.Body, AssistantVoice.MaxAudioBytes, ct);
            return Results.Ok(await voice.TranscribeAsync(audio, http.ContentType, language, ct));
        }).DisableAntiforgery();

        root.MapPost("/speak", async (SpeakRequest request, AssistantVoice voice, HttpContext http, CancellationToken ct) =>
        {
            var spoken = await voice.SpeakAsync(request, ct);
            http.Response.Headers.CacheControl = "no-store";
            return Results.File(spoken.Audio, spoken.MediaType);
        });

        root.MapGet("/settings", async (AssistantHistory history, CancellationToken ct) => Results.Ok(await history.SettingsAsync(ct)));

        root.MapPut("/settings", async (AssistantSettingsRequest request, AssistantHistory history, IDbSession session, CancellationToken ct) =>
        {
            var view = await history.ChangeSettingsAsync(request, ct);
            await session.CommitAsync(ct);
            return Results.Ok(view);
        });

        // Actions an external AI client asked for and the employee confirms or rejects inside TeamBeats (spec 035 FR-022).
        var actions = app.MapGroup("/api/v1/me/ai-actions").RequireTenantUser().WithTags("Agents");
        actions.MapGet("/", async (ExternalActions external, CancellationToken ct) => Results.Ok(new { items = await external.PendingAsync(ct) }));
        actions.MapGet("/{id:guid}", async (Guid id, ExternalActions external, CancellationToken ct) => Results.Ok(await external.GetAsync(id, ct)));
        actions.MapPost("/{id:guid}/decision", async (Guid id, ExternalActionDecision decision, ExternalActions external, IDbSession session, CancellationToken ct) =>
        {
            var view = await external.DecideAsync(id, decision, ct);
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

        // The marketplace (020 US6): listing, favourites, ratings and access requests.
        // Persistent memory (020 US9): the person's own notes, acceptance per agent, edit and delete.
        agents.MapGet("/memory", async (AgentMemory memory, CancellationToken ct) => Results.Ok(await memory.MineAsync(ct)));
        agents.MapGet("/{id:guid}/memory", async (Guid id, AgentMemory memory, CancellationToken ct) => Results.Ok(await memory.ViewAsync(id, ct)));
        agents.MapPut("/{id:guid}/memory/consent", async (Guid id, AgentMemory memory, IDbSession session, CancellationToken ct) =>
        {
            var view = await memory.AcceptAsync(id, ct);
            await session.CommitAsync(ct);
            return Results.Ok(view);
        });
        agents.MapDelete("/{id:guid}/memory/consent", async (Guid id, AgentMemory memory, IDbSession session, CancellationToken ct) =>
        {
            await memory.WithdrawAsync(id, ct);
            await session.CommitAsync(ct);
            return Results.NoContent();
        });
        agents.MapPut("/memory/{itemId:guid}", async (Guid itemId, MemoryItemRequest request, AgentMemory memory, IDbSession session, CancellationToken ct) =>
        {
            var view = await memory.UpdateAsync(itemId, request, ct);
            await session.CommitAsync(ct);
            return Results.Ok(view);
        });
        agents.MapDelete("/memory/{itemId:guid}", async (Guid itemId, AgentMemory memory, IDbSession session, CancellationToken ct) =>
        {
            await memory.DeleteAsync(itemId, ct);
            await session.CommitAsync(ct);
            return Results.NoContent();
        });

        // Daily briefings (020 US8): opt in at a time on chosen days, opt out.
        agents.MapGet("/{id:guid}/briefing", async (Guid id, AgentBriefings briefings, CancellationToken ct) => Results.Ok(await briefings.GetAsync(id, ct)));
        agents.MapPut("/{id:guid}/briefing", async (Guid id, BriefingRequest request, AgentBriefings briefings, IDbSession session, CancellationToken ct) =>
        {
            var view = await briefings.SubscribeAsync(id, request, ct);
            await session.CommitAsync(ct);
            return Results.Ok(view);
        });
        agents.MapDelete("/{id:guid}/briefing", async (Guid id, AgentBriefings briefings, IDbSession session, CancellationToken ct) =>
        {
            await briefings.UnsubscribeAsync(id, ct);
            await session.CommitAsync(ct);
            return Results.NoContent();
        });

        agents.MapGet("/marketplace", async (string? q, string? category, AgentMarketplace market, CancellationToken ct) => Results.Ok(await market.ListAsync(q, category, ct)));
        agents.MapPut("/{id:guid}/favourite", async (Guid id, AgentMarketplace market, IDbSession session, CancellationToken ct) =>
        {
            await market.SetFavouriteAsync(id, true, ct);
            await session.CommitAsync(ct);
            return Results.NoContent();
        });
        agents.MapDelete("/{id:guid}/favourite", async (Guid id, AgentMarketplace market, IDbSession session, CancellationToken ct) =>
        {
            await market.SetFavouriteAsync(id, false, ct);
            await session.CommitAsync(ct);
            return Results.NoContent();
        });
        agents.MapPut("/{id:guid}/rating", async (Guid id, RatingRequest request, AgentMarketplace market, IDbSession session, CancellationToken ct) =>
        {
            await market.RateAsync(id, request, ct);
            await session.CommitAsync(ct);
            return Results.NoContent();
        });
        agents.MapGet("/{id:guid}/ratings", async (Guid id, AgentMarketplace market, CancellationToken ct) => Results.Ok(await market.RatingsAsync(id, ct)));
        agents.MapPost("/{id:guid}/access-requests", async (Guid id, AgentMarketplace market, IDbSession session, CancellationToken ct) =>
        {
            var view = await market.RequestAccessAsync(id, ct);
            await session.CommitAsync(ct);
            return Results.Ok(view);
        });
        agents.MapGet("/{id:guid}/access-requests", async (Guid id, AgentMarketplace market, CancellationToken ct) =>
            Results.Ok(new { requests = await market.PendingAsync(id, ct) }));
        agents.MapPost("/access-requests/{id:guid}/decision", async (Guid id, AccessDecision decision, AgentMarketplace market, IDbSession session, CancellationToken ct) =>
        {
            var view = await market.DecideAsync(id, decision, ct);
            await session.CommitAsync(ct);
            return Results.Ok(view);
        });

        agents.MapGet("/studio", async (AgentRegistry registry, CancellationToken ct) => Results.Ok(await registry.StudioAsync(ct)));

        agents.MapPost("/{id:guid}/clone", async (Guid id, CloneRequest request, AgentRegistry registry, IDbSession session, CancellationToken ct) =>
        {
            var detail = await registry.CloneAsync(id, request, ct);
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
        // With Accept: text/event-stream the turn streams (020 T021): "run" with its ID, "step" for each tool decision,
        // "delta" with answer text as it arrives, then "done" with the checked answer. The run goes on when the person
        // leaves the page; they read it later from its result.
        agents.MapPost("/{id:guid}/runs", async (Guid id, RunRequest request, HttpContext http, AgentRuns runs, IDbSession session, ILoggerFactory logs,
            CancellationToken ct) =>
        {
            if (!WantsStream(http.Request))
            {
                var view = await runs.RunAsync(id, request, ct);
                await session.CommitAsync(ct);
                return Results.Ok(view);
            }

            var turn = await runs.StreamAsync(id, request, ct);
            var response = http.Response;
            response.ContentType = "text/event-stream";
            response.Headers.CacheControl = "no-cache, no-transform";
            response.Headers["X-Accel-Buffering"] = "no";
            http.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();
            var connected = true;
            async Task SendAsync(string name, object data)
            {
                if (!connected)
                {
                    return;
                }
                try
                {
                    await WriteEventAsync(response, name, data, ct);
                }
                catch (Exception ex) when (ex is OperationCanceledException or IOException or InvalidOperationException)
                {
                    connected = false;
                }
            }
            try
            {
                var done = await turn(SendAsync, CancellationToken.None);
                await session.CommitAsync(CancellationToken.None);
                await SendAsync("done", done);
            }
            catch (Exception ex)
            {
                // Headers are sent, so a problem response is no longer possible; what was checkpointed stays and resumes.
                logs.CreateLogger("TeamBeats.Agents").LogError(ex, "Agent run failed while streaming");
                await SendAsync("error", new { code = ex is TeamBeats.Primitives.AppException app ? app.DetailKey ?? app.Code : "failed" });
            }
            return Results.Empty;
        });

        agents.MapGet("/runs/{id:guid}/result", async (Guid id, AgentRuns runs, CancellationToken ct) => Results.Ok(await runs.ResultAsync(id, ct)));
        agents.MapGet("/approvals/{id:guid}", async (Guid id, AgentApprovals approvals, CancellationToken ct) => Results.Ok(await approvals.GetAsync(id, ct)));

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

        agents.MapPost("/{id:guid}/revoke-tokens", async (Guid id, ReasonRequest request, AgentLifecycle lifecycle, IDbSession session, CancellationToken ct) =>
        {
            var detail = await lifecycle.RevokeTokensAsync(id, request, ct);
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
            await lifecycle.SuspendBreakingAsync(null, ct);
            await session.CommitAsync(ct);
            return Results.Ok(view);
        });

        // Org unit agent policies (027 US8 scenario 3): only stricter than the policy above, applied to the unit's agents.
        agents.MapGet("/policy/units/{unitId:guid}", async (Guid unitId, UnitAgentPolicyService units, CancellationToken ct) => Results.Ok(await units.GetAsync(unitId, ct)));

        agents.MapPut("/policy/units/{unitId:guid}", async (Guid unitId, UnitAgentPolicyRequest request, UnitAgentPolicyService units, AgentLifecycle lifecycle,
            IDbSession session, CancellationToken ct) =>
        {
            var view = await units.ChangeAsync(unitId, request, ct);
            await lifecycle.SuspendBreakingAsync(unitId, ct);
            await session.CommitAsync(ct);
            return Results.Ok(view);
        });

        agents.MapDelete("/policy/units/{unitId:guid}", async (Guid unitId, UnitAgentPolicyService units, IDbSession session, CancellationToken ct) =>
        {
            await units.DeleteAsync(unitId, ct);
            await session.CommitAsync(ct);
            return Results.NoContent();
        });

        // The Agent Security Center (027 FR-023): alerts, incidents, evidence export and the watch list.
        var security = agents.MapGroup("/security");
        security.MapGet("/alerts", async (string? status, Guid? agentId, int? limit, AgentSecurityCenter center, CancellationToken ct) =>
            Results.Ok(await center.AlertsAsync(status, agentId, limit ?? 50, ct)));
        security.MapGet("/alerts/{id:guid}", async (Guid id, AgentSecurityCenter center, CancellationToken ct) => Results.Ok(await center.AlertAsync(id, ct)));
        security.MapPost("/alerts/{id:guid}/dismiss", async (Guid id, AgentSecurityCenter center, IDbSession session, CancellationToken ct) =>
        {
            var view = await center.DismissAsync(id, ct);
            await session.CommitAsync(ct);
            return Results.Ok(view);
        });
        security.MapGet("/incidents", async (string? status, int? limit, AgentSecurityCenter center, CancellationToken ct) =>
            Results.Ok(await center.IncidentsAsync(status, limit ?? 50, ct)));
        security.MapPost("/incidents", async (OpenIncidentRequest request, AgentSecurityCenter center, IDbSession session, CancellationToken ct) =>
        {
            var view = await center.OpenIncidentAsync(request, ct);
            await session.CommitAsync(ct);
            return Results.Created($"/api/v1/agents/security/incidents/{view.Id}", view);
        });
        security.MapGet("/incidents/{id:guid}", async (Guid id, AgentSecurityCenter center, CancellationToken ct) => Results.Ok(await center.IncidentAsync(id, ct)));
        security.MapPatch("/incidents/{id:guid}", async (Guid id, IncidentChangeRequest request, AgentSecurityCenter center, IDbSession session, CancellationToken ct) =>
        {
            var view = await center.ChangeIncidentAsync(id, request, ct);
            await session.CommitAsync(ct);
            return Results.Ok(view);
        });
        security.MapPost("/incidents/{id:guid}/close", async (Guid id, CloseIncidentRequest request, AgentSecurityCenter center, IDbSession session, CancellationToken ct) =>
        {
            var view = await center.CloseIncidentAsync(id, request, ct);
            await session.CommitAsync(ct);
            return Results.Ok(view);
        });
        security.MapGet("/incidents/{id:guid}/evidence", async (Guid id, AgentSecurityCenter center, AgentRuns runs, IDbSession session, CancellationToken ct) =>
        {
            var evidence = await center.EvidenceAsync(id, runs, ct);
            await session.CommitAsync(ct);
            return Results.Json(evidence, contentType: "application/json", statusCode: 200);
        });
        security.MapGet("/watch", async (AgentSecurityCenter center, CancellationToken ct) => Results.Ok(await center.WatchListAsync(ct)));

        app.MapAssistantPublicEndpoints();
        return app;
    }
}
