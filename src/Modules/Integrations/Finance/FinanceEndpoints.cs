using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using TeamBeats.Authorization.Contracts;
using TeamBeats.Messaging;
using TeamBeats.Modules.Integrations.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Primitives;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.Integrations.Finance;

/// <summary>The hand-offs sent through a finance connection (spec 017 US7 scenario 2, permission <c>integration.manage</c>), newest first, without claim content.</summary>
public static class FinanceEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        app.MapGet("/api/v1/integrations/pack-connections/{id:guid}/finance-handoffs", async (Guid id, IAuthorizationService authz, IDbSession session,
            FinanceHandOffs handOffs, CancellationToken ct) =>
        {
            await authz.DemandAsync(Permissions.IntegrationManage, AuthzTarget.Tenant, ct);
            if (!await session.ScalarAsync<bool>("select exists (select 1 from integrations.connection where id = @id and kind = @kind)",
                new { id, kind = FinancePack.KindName }, ct))
            {
                throw AppException.NotFound();
            }
            return Results.Ok(await handOffs.ListAsync(id, ct));
        }).RequireTenantUser().InAdminArea().WithTags("Integrations: connector packs");
    }
}

/// <summary>Stores the outcome of a finished submit job.</summary>
public sealed class FinanceSubmitFinished(FinanceHandOffs handOffs) : IMessageHandler<ConnectorActionCompleted>
{
    public Task HandleAsync(ConnectorActionCompleted message, CancellationToken ct) => handOffs.OnCompletedAsync(message, ct);
}

/// <summary>Deleting a finance connection drops the record of its hand-offs, so the delete preview names them.</summary>
public sealed class FinanceDependents(IDbSession session) : IConnectionDependents
{
    public async Task<IReadOnlyList<ConnectionDependent>> DescribeAsync(Guid connectionId, CancellationToken ct = default)
    {
        var count = await session.ScalarAsync<long>("select count(*) from integrations.finance_handoff where connection_id = @connectionId", new { connectionId }, ct);
        return count > 0 ? [new ConnectionDependent("integrations.dependent.finance_handoffs", count)] : [];
    }
}
