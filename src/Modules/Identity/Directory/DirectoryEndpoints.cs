using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using TeamBeats.Authorization.Contracts;
using TeamBeats.Entitlements.Contracts;
using TeamBeats.Messaging;
using TeamBeats.Modules.Integrations.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Primitives;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.Identity.Directory;

/// <summary>Directory sync administration (spec 003 US6): settings, recent runs and "sync now".</summary>
public static class DirectoryEndpoints
{
    public static IEndpointRouteBuilder MapDirectoryEndpoints(this IEndpointRouteBuilder app)
    {
        var admin = app.MapGroup("/api/v1/tenant/directory").RequireTenantUser().InAdminArea().WithTags("Tenant: identity");

        admin.MapGet("/", async (IAuthorizationService authz, IConnectionFeatures connections, DirectorySyncService sync, CancellationToken ct) =>
        {
            await authz.DemandAsync(Permissions.IdentityConfigManage, AuthzTarget.Tenant, ct);
            var feature = await connections.FindAsync(ConnectionKinds.Microsoft365, Microsoft365Features.DirectorySync, ct);
            var settings = await sync.GetSettingsAsync(ct);
            var runs = settings is null ? [] : await sync.RecentRunsAsync(settings.ConnectionId, 10, ct);
            return Results.Ok(new DirectoryView(feature?.ConnectionId, feature?.Usable ?? false, settings, runs));
        });

        admin.MapPut("/", async (DirectorySettingsRequest request, IAuthorizationService authz, IEntitlementService entitlements,
            DirectorySyncService sync, IDbSession session, CancellationToken ct) =>
        {
            await authz.DemandAsync(Permissions.IdentityConfigManage, AuthzTarget.Tenant, ct);
            await entitlements.DemandAsync(Capabilities.IdentityDirectorySync, ct);
            var saved = await sync.SaveSettingsAsync(request, ct);
            await session.CommitAsync(ct);
            return Results.Ok(saved);
        }).RequireStepUp();

        admin.MapPost("/sync", async (IAuthorizationService authz, IEntitlementService entitlements, DirectorySyncService sync,
            IOutbox outbox, IDbSession session, CancellationToken ct) =>
        {
            await authz.DemandAsync(Permissions.IdentityConfigManage, AuthzTarget.Tenant, ct);
            await entitlements.DemandAsync(Capabilities.IdentityDirectorySync, ct);
            var settings = await sync.GetSettingsAsync(ct) ?? throw AppException.Conflict("conflict.directory.not_configured");
            await outbox.EnqueueAsync(new DirectorySyncRequested(settings.ConnectionId), ct);
            await session.CommitAsync(ct);
            return Results.Accepted();
        });

        return app;
    }
}

public sealed record DirectoryView(Guid? ConnectionId, bool ConnectionUsable, DirectorySettings? Settings, IReadOnlyList<SyncRun> Runs);

/// <summary>Runs a requested sync on the worker.</summary>
public sealed class RunRequestedDirectorySync(DirectorySyncService sync) : IMessageHandler<DirectorySyncRequested>
{
    public async Task HandleAsync(DirectorySyncRequested message, CancellationToken ct) => await sync.RunAsync(message.ConnectionId, ct);
}

/// <summary>Removes sync data when the Microsoft 365 connection is deleted; users are kept.</summary>
public sealed class RemoveDirectoryOnConnectionDeleted(DirectorySyncService sync) : IMessageHandler<ConnectionDeleted>
{
    public InactiveTenantPolicy InactiveTenantPolicy => InactiveTenantPolicy.Process;

    public async Task HandleAsync(ConnectionDeleted message, CancellationToken ct) => await sync.RemoveConnectionAsync(message.ConnectionId, ct);
}

/// <summary>Tells the Connections area how many users a connection keeps in step before it is deleted (spec 006 US5 scenario 8).</summary>
public sealed class DirectoryConnectionDependents(IDbSession session) : IConnectionDependents
{
    public async Task<IReadOnlyList<ConnectionDependent>> DescribeAsync(Guid connectionId, CancellationToken ct = default)
    {
        var count = await session.ScalarAsync<long>("select count(*) from identity.directory_user_link where connection_id = @c", new { c = connectionId }, ct);
        return count == 0 ? [] : [new ConnectionDependent("integrations.dependent.synced_users", count)];
    }
}
