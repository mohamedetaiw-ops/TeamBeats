using TeamBeats.Audit.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Primitives;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.PublicApi;

/// <summary>A client ID the platform blocked, with why and when.</summary>
public sealed record PlatformBlock(string ClientId, string Reason, Guid BlockedBy, DateTimeOffset BlockedAt);

public sealed record PlatformBlockRequest(string? ClientId, string? Reason);

/// <summary>
/// The platform block (spec 035 FR-028, US6 scenario 4): the platform operator stops a client ID in every tenant at
/// once, for a vulnerability or abuse. Apps read it through the registration lookup, so new tokens, existing tokens and
/// MCP calls are all refused on their next use; the tenant's app page shows the app as blocked. Platform audit records
/// each block and unblock with its reason.
/// </summary>
public sealed class PlatformBlocks(IDbSession session, ITenantContext context, IAuditWriter audit, IClock clock)
{
    public Task<bool> IsBlockedAsync(string clientId, CancellationToken ct) =>
        session.ScalarAsync<bool>("select exists (select 1 from publicapi.platform_block where client_id = @clientId)", new { clientId }, ct);

    public Task<List<PlatformBlock>> ListAsync(CancellationToken ct) =>
        session.QueryAsync("select client_id, reason, blocked_by, blocked_at from publicapi.platform_block order by blocked_at desc, client_id", null,
            r => new PlatformBlock(r.GetString(0), r.GetString(1), r.GetGuid(2), r.GetFieldValue<DateTimeOffset>(3)), ct);

    public async Task<PlatformBlock> BlockAsync(PlatformBlockRequest request, CancellationToken ct)
    {
        var errors = new ValidationErrors();
        var clientId = request.ClientId?.Trim() ?? "";
        if (clientId.Length is < 1 or > VerifiedMcpClients.MaxClientIdLength)
        {
            errors.Add("clientId", "validation.api.block_client_id");
        }
        var reason = request.Reason?.Trim() ?? "";
        if (reason.Length is < 1 or > 500)
        {
            errors.Add("reason", "validation.api.block_reason");
        }
        errors.ThrowIfAny();
        var block = new PlatformBlock(clientId, reason, context.ActorId ?? throw AppException.Unauthenticated(), clock.UtcNow);
        await session.ExecAsync(
            """
            insert into publicapi.platform_block (client_id, reason, blocked_by, blocked_at) values (@ClientId, @Reason, @BlockedBy, @BlockedAt)
            on conflict (client_id) do nothing
            """, block, ct);
        await audit.WritePlatformAsync(new AuditEntry(AuditActions.ApiClientBlocked, "api_client", clientId, Changes: new { reason }), null, ct);
        return block;
    }

    public async Task UnblockAsync(string clientId, CancellationToken ct)
    {
        if (await session.ExecAsync("delete from publicapi.platform_block where client_id = @clientId", new { clientId }, ct) == 0)
        {
            throw AppException.NotFound();
        }
        await audit.WritePlatformAsync(new AuditEntry(AuditActions.ApiClientUnblocked, "api_client", clientId), null, ct);
    }
}
