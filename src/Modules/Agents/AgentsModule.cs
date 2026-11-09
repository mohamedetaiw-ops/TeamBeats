using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
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

        return app;
    }
}
