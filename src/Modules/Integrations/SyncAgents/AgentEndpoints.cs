using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using TeamBeats.Authorization.Contracts;
using TeamBeats.Modules.Integrations.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Primitives;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.Integrations.SyncAgents;

/// <summary>
/// The on-premises sync agent's API (spec 017 US6) and the administrator endpoints for its agents. Agent routes have no
/// session: the tenant comes from the host name, which must be a tenant host of an active tenant, and every request
/// except enrolment carries the agent's signature (see <see cref="AgentProtocol"/>). Administrator routes need
/// <c>integration.manage</c>; making a code and revoking an agent need step-up.
/// </summary>
public static class AgentEndpoints
{
    public const int MaxBodyBytes = 4 * 1024 * 1024;

    public static void Map(IEndpointRouteBuilder app)
    {
        var agent = app.MapGroup("/api/v1/agent").WithTags("Integrations: sync agent").DisableAntiforgery();

        agent.MapPost("/enrol", async (HttpRequest request, ITenantContext context, TenantContextAccessor accessor, SyncAgentService agents,
            IDbSession session, CancellationToken ct) =>
        {
            EnterTenant(context, accessor);
            var body = await ReadAsync(request, ct);
            var enrolled = await agents.EnrolAsync(Deserialize<EnrolRequest>(body) ?? new EnrolRequest(null, null, null, null), ct);
            await session.CommitAsync(ct);
            return Results.Ok(enrolled);
        });

        agent.MapPost("/heartbeat", (HttpRequest request, ITenantContext context, TenantContextAccessor accessor, SyncAgentService agents,
            IDbSession session, CancellationToken ct) => Signed(request, context, accessor, agents, session, async (a, body) =>
            {
                await agents.HeartbeatAsync(a, Deserialize<HeartbeatRequest>(body), ct);
                return Results.NoContent();
            }, ct));

        agent.MapPost("/jobs:pull", (HttpRequest request, ITenantContext context, TenantContextAccessor accessor, SyncAgentService agents,
            IDbSession session, CancellationToken ct) => Signed(request, context, accessor, agents, session, async (a, _) =>
            {
                var job = await agents.PullAsync(a, ct);
                return job is null ? Results.NoContent() : Results.Ok(job);
            }, ct));

        agent.MapPost("/jobs/{jobId:guid}/records", (Guid jobId, HttpRequest request, ITenantContext context, TenantContextAccessor accessor,
            SyncAgentService agents, IDbSession session, CancellationToken ct) => Signed(request, context, accessor, agents, session, async (a, body) =>
            {
                var stored = await agents.RecordsAsync(a, jobId, Deserialize<AgentRecordBatch>(body), ct);
                return Results.Ok(new { stored });
            }, ct));

        agent.MapPost("/jobs/{jobId:guid}:complete", (Guid jobId, HttpRequest request, ITenantContext context, TenantContextAccessor accessor,
            SyncAgentService agents, IDbSession session, CancellationToken ct) => Signed(request, context, accessor, agents, session, async (a, body) =>
            {
                await agents.CompleteAsync(a, jobId, Deserialize<AgentJobResult>(body), ct);
                return Results.NoContent();
            }, ct));

        var admin = app.MapGroup("/api/v1/integrations/pack-connections/{id:guid}").RequireTenantUser().InAdminArea().WithTags("Integrations: sync agent");

        admin.MapPost("/agents/enrolment-code", async (Guid id, IAuthorizationService authz, SyncAgentService agents, IDbSession session, CancellationToken ct) =>
        {
            await Demand(authz, ct);
            var code = await agents.CreateEnrolmentCodeAsync(id, ct);
            await session.CommitAsync(ct);
            return Results.Ok(code);
        }).RequireStepUp();

        admin.MapGet("/agents", async (Guid id, IAuthorizationService authz, SyncAgentService agents, CancellationToken ct) =>
        {
            await Demand(authz, ct);
            return Results.Ok(await agents.ListAsync(id, ct));
        });

        admin.MapPost("/agents/{agentId:guid}:revoke", async (Guid id, Guid agentId, IAuthorizationService authz, SyncAgentService agents,
            IDbSession session, CancellationToken ct) =>
        {
            await Demand(authz, ct);
            await agents.RevokeAsync(id, agentId, ct);
            await session.CommitAsync(ct);
            return Results.NoContent();
        }).RequireStepUp();

        admin.MapGet("/agent-jobs", async (Guid id, IAuthorizationService authz, SyncAgentService agents, CancellationToken ct) =>
        {
            await Demand(authz, ct);
            return Results.Ok(await agents.JobsAsync(id, ct));
        });

        admin.MapPost("/agent-jobs", async (Guid id, IAuthorizationService authz, SyncAgentService agents, IDbSession session, CancellationToken ct) =>
        {
            await Demand(authz, ct);
            var job = await agents.EnqueueAsync(id, ct);
            await session.CommitAsync(ct);
            return Results.Created($"/api/v1/integrations/pack-connections/{id}/agent-jobs", job);
        });
    }

    private static Task Demand(IAuthorizationService authz, CancellationToken ct) => authz.DemandAsync(Permissions.IntegrationManage, AuthzTarget.Tenant, ct);

    private static void EnterTenant(ITenantContext context, TenantContextAccessor accessor)
    {
        if (context.Host.Kind != HostScopeKind.Tenant || context.Host.Tenant is not { Status: TenantLifecycleStatus.Active } tenant)
        {
            throw AppException.NotFound();
        }
        accessor.SetTenant(tenant.Id);
        accessor.SetActor(ActorType.Anonymous, null);
    }

    private static async Task<IResult> Signed(HttpRequest request, ITenantContext context, TenantContextAccessor accessor, SyncAgentService agents,
        IDbSession session, Func<AuthenticatedAgent, byte[], Task<IResult>> handle, CancellationToken ct)
    {
        EnterTenant(context, accessor);
        var body = await ReadAsync(request, ct);
        var pathAndQuery = request.Path.ToString() + request.QueryString.ToString();
        var agent = await agents.AuthenticateAsync(request.Headers[AgentProtocol.AgentHeader], request.Headers[AgentProtocol.TimestampHeader],
            request.Headers[AgentProtocol.SignatureHeader], request.Method, pathAndQuery, body, ct);
        var result = await handle(agent, body);
        await session.CommitAsync(ct);
        return result;
    }

    private static T? Deserialize<T>(byte[] body) where T : class
    {
        if (body.Length == 0)
        {
            return null;
        }
        try
        {
            return JsonSerializer.Deserialize<T>(body, JsonDefaults.Options);
        }
        catch (JsonException)
        {
            throw AppException.Validation("body", "validation.sync_agent.body");
        }
    }

    private static async Task<byte[]> ReadAsync(HttpRequest request, CancellationToken ct)
    {
        if (request.ContentLength is > MaxBodyBytes)
        {
            throw new AppException(ProblemCodes.ValidationFailed, 413, "validation.sync_agent.body_too_large");
        }
        using var buffer = new MemoryStream();
        var chunk = new byte[64 * 1024];
        int read;
        while ((read = await request.Body.ReadAsync(chunk, ct)) > 0)
        {
            buffer.Write(chunk, 0, read);
            if (buffer.Length > MaxBodyBytes)
            {
                throw new AppException(ProblemCodes.ValidationFailed, 413, "validation.sync_agent.body_too_large");
            }
        }
        return buffer.ToArray();
    }
}
