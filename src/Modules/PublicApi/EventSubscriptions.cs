using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TeamBeats.Audit.Contracts;
using TeamBeats.Authorization.Contracts;
using TeamBeats.Entitlements.Contracts;
using TeamBeats.Messaging;
using TeamBeats.Modules.Content.Contracts;
using TeamBeats.Modules.Documents.Contracts;
using TeamBeats.Modules.Integrations.Contracts;
using TeamBeats.Modules.Notifications.Contracts;
using TeamBeats.Modules.People.Contracts;
using TeamBeats.Modules.Policies.Contracts;
using TeamBeats.Modules.PublicApi.Contracts;
using TeamBeats.Modules.Services.Contracts;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Primitives;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.PublicApi;

/// <summary>A subscription to add: one event type of the catalogue, the HTTPS URL that receives it and the secret manager reference that signs it.</summary>
public sealed record EventSubscriptionRequest(string? EventType, string? Url, string? SecretRef);

/// <summary>An event subscription as the admin area shows it. The signing secret is shown by reference only.</summary>
public sealed record EventSubscriptionView(Guid Id, Guid AppId, string AppName, string EventType, string Url, string SecretRef, string Status,
    DateTimeOffset? FailingSince, DateTimeOffset? PausedAt, DateTimeOffset CreatedAt);

/// <summary>An event of the catalogue, with whether the tenant's package includes what it needs.</summary>
public sealed record EventCatalogueEntry(string Type, int Version, string ResourceType, string ReadScope, bool Available);

/// <summary>An app's own subscription as the public API shows it to the app.</summary>
public sealed record PublicEventSubscription(Guid Id, string EventType, string Url, string Status, DateTimeOffset CreatedAt);

/// <summary>One delivery of a subscription, from the delivery log of the webhook runtime.</summary>
public sealed record EventDeliveryView(Guid Id, string? EventType, string Status, int Attempts, string? ErrorClass, DateTimeOffset CreatedAt, DateTimeOffset? FinishedAt);

/// <summary>
/// Event subscriptions of application apps (spec 035 US4, FR-018). An API administrator subscribes an app that holds
/// <c>events:subscribe</c> and the read scope of the event's resource to one event type of the public catalogue; the URL
/// must be on the tenant's host allowlist and the signing secret is a reference in the tenant's secret manager, as for
/// every webhook of spec 017. The app itself lists and removes its own subscriptions through the public API.
/// </summary>
public sealed class EventSubscriptions(
    IDbSession session,
    ITenantContext context,
    IAuthorizationService authz,
    IEntitlementService entitlements,
    IAuditWriter audit,
    IWebhookDelivery delivery,
    Apps apps)
{
    public const int MaxPerApp = 20;

    private const string Columns =
        """
        s.id, s.app_id, a.name, s.event_type, s.url, s.secret_ref, s.status, s.failing_since, s.paused_at, s.created_at
        """;

    private static EventSubscriptionView Read(Npgsql.NpgsqlDataReader r) => new(r.GetGuid(0), r.GetGuid(1), r.GetString(2), r.GetString(3), r.GetString(4),
        r.GetString(5), r.GetString(6), r.IsDBNull(7) ? null : r.GetFieldValue<DateTimeOffset>(7), r.IsDBNull(8) ? null : r.GetFieldValue<DateTimeOffset>(8),
        r.GetFieldValue<DateTimeOffset>(9));

    public async Task<IReadOnlyList<EventCatalogueEntry>> CatalogueAsync(CancellationToken ct)
    {
        await DemandAsync(Permissions.ApiView, ct);
        return await AvailableAsync(ct);
    }

    /// <summary>The catalogue with what the package includes; no permission check (the public API's own list).</summary>
    public async Task<IReadOnlyList<EventCatalogueEntry>> AvailableAsync(CancellationToken ct)
    {
        var events = await entitlements.IsEnabledAsync(Capabilities.ApiEventSubscriptions, ct);
        var list = new List<EventCatalogueEntry>();
        foreach (var e in PublicEventCatalogue.All)
        {
            var available = events;
            foreach (var capability in ScopeCatalogue.Find(e.ReadScope)?.Capabilities ?? [])
            {
                available = available && await entitlements.IsEnabledAsync(capability, ct);
            }
            list.Add(new EventCatalogueEntry(e.Type, e.Version, e.ResourceType, e.ReadScope, available));
        }
        return list;
    }

    public async Task<IReadOnlyList<EventSubscriptionView>> ListAsync(Guid? appId, CancellationToken ct)
    {
        await DemandAsync(Permissions.ApiView, ct);
        return await session.QueryAsync(
            $"select {Columns} from publicapi.event_subscription s join publicapi.app a on a.id = s.app_id where @appId::uuid is null or s.app_id = @appId order by s.created_at, s.id",
            new { appId }, Read, ct);
    }

    public async Task<EventSubscriptionView> CreateAsync(Guid appId, EventSubscriptionRequest request, CancellationToken ct)
    {
        await DemandAsync(Permissions.ApiManage, ct);
        await entitlements.DemandAsync(Capabilities.ApiEventSubscriptions, ct);
        var app = await apps.FindAsync(appId, ct) ?? throw AppException.NotFound();
        var errors = new ValidationErrors();
        var definition = PublicEventCatalogue.Find(request.EventType?.Trim());
        if (app.Kind != AppKinds.Application)
        {
            errors.Add("appId", "validation.api.events_application_only");
        }
        else if (!app.Scopes.Contains(ScopeCatalogue.EventsSubscribe))
        {
            errors.Add("appId", "validation.api.events_scope");
        }
        if (definition is null)
        {
            // Only the public event catalogue may be subscribed to (US4 scenario 5).
            errors.Add("eventType", "validation.api.event_unknown");
        }
        else if (!app.Scopes.Contains(definition.ReadScope))
        {
            errors.Add("eventType", "validation.api.event_read_scope");
        }
        errors.ThrowIfAny();
        foreach (var capability in ScopeCatalogue.Find(definition!.ReadScope)?.Capabilities ?? [])
        {
            await entitlements.DemandAsync(capability, ct);
        }

        await session.ExecAsync("select pg_advisory_xact_lock(hashtextextended(@key, 0))", new { key = "publicapi.subscriptions:" + appId }, ct);
        if (await session.ScalarAsync<long>("select count(*) from publicapi.event_subscription where app_id = @appId", new { appId }, ct) >= MaxPerApp)
        {
            throw AppException.Conflict("conflict.api.subscription_limit");
        }
        var url = Uri.TryCreate(request.Url?.Trim(), UriKind.Absolute, out var parsed) ? parsed.AbsoluteUri : null;
        if (url is not null && await session.ScalarAsync<bool>(
            "select exists (select 1 from publicapi.event_subscription where app_id = @appId and event_type = @type and url = @url)",
            new { appId, type = definition.Type, url }, ct))
        {
            throw AppException.Conflict("conflict.api.subscription_exists");
        }
        // The endpoint checks the URL (HTTPS, on the allowlist) and the secret reference as for every webhook (spec 017).
        var endpoint = await delivery.CreateEndpointAsync(new WebhookEndpointRequest($"{app.Name}: {definition.Type}", request.Url?.Trim(), request.SecretRef?.Trim()), ct);
        if (url is null)
        {
            throw AppException.Validation("url", "validation.webhook.url");
        }
        var id = Ids.New();
        await session.ExecAsync(
            """
            insert into publicapi.event_subscription (tenant_id, id, app_id, event_type, url, secret_ref, endpoint_id, status, created_by)
            values (platform.current_tenant_id(), @id, @appId, @type, @url, @secretRef, @endpoint, 'active', @actor)
            """, new { id, appId, type = definition.Type, url, secretRef = request.SecretRef!.Trim(), endpoint, actor = context.RequireUser() }, ct);
        await audit.WriteAsync(new AuditEntry(AuditActions.ApiSubscriptionCreated, "api_subscription", id.ToString(),
            Changes: new { appId, app = app.Name, eventType = definition.Type, host = new Uri(url).Host }), ct);
        return await GetAsync(id, ct) ?? throw AppException.NotFound();
    }

    /// <summary>Starts a paused subscription again; the failure count starts afresh.</summary>
    public async Task<EventSubscriptionView> ResumeAsync(Guid id, CancellationToken ct)
    {
        await DemandAsync(Permissions.ApiManage, ct);
        var s = await GetAsync(id, ct) ?? throw AppException.NotFound();
        if (s.Status == "active")
        {
            return s;
        }
        await session.ExecAsync("update publicapi.event_subscription set status = 'active', failing_since = null, paused_at = null where id = @id", new { id }, ct);
        await delivery.SetEndpointEnabledAsync(await EndpointAsync(id, ct), true, ct);
        await audit.WriteAsync(new AuditEntry(AuditActions.ApiSubscriptionResumed, "api_subscription", id.ToString(),
            Changes: new { s.AppId, s.EventType, host = new Uri(s.Url).Host }), ct);
        return (await GetAsync(id, ct))!;
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct)
    {
        await DemandAsync(Permissions.ApiManage, ct);
        await RemoveAsync(await GetAsync(id, ct) ?? throw AppException.NotFound(), ct);
    }

    public async Task<IReadOnlyList<EventDeliveryView>> DeliveriesAsync(Guid id, CancellationToken ct)
    {
        await DemandAsync(Permissions.ApiView, ct);
        _ = await GetAsync(id, ct) ?? throw AppException.NotFound();
        return (await delivery.DeliveriesAsync(await EndpointAsync(id, ct), 50, ct))
            .Select(d => new EventDeliveryView(d.RunId, d.EventType, d.Status, d.Attempts, d.ErrorClass, d.CreatedAt, d.FinishedAt)).ToList();
    }

    /// <summary>The calling app's own subscriptions (<c>events:subscribe</c>).</summary>
    public async Task<IReadOnlyList<PublicEventSubscription>> OwnAsync(Guid appId, CancellationToken ct) =>
        (await session.QueryAsync(
            $"select {Columns} from publicapi.event_subscription s join publicapi.app a on a.id = s.app_id where s.app_id = @appId order by s.created_at, s.id",
            new { appId }, Read, ct)).Select(s => new PublicEventSubscription(s.Id, s.EventType, s.Url, s.Status, s.CreatedAt)).ToList();

    /// <summary>Removes one of the calling app's own subscriptions; another app's is not found.</summary>
    public async Task DeleteOwnAsync(Guid appId, Guid id, CancellationToken ct)
    {
        var s = await GetAsync(id, ct);
        if (s is null || s.AppId != appId)
        {
            throw AppException.NotFound();
        }
        await RemoveAsync(s, ct);
    }

    private async Task RemoveAsync(EventSubscriptionView s, CancellationToken ct)
    {
        var endpoint = await EndpointAsync(s.Id, ct);
        await session.ExecAsync("delete from publicapi.event_subscription where id = @id", new { id = s.Id }, ct);
        await delivery.DeleteEndpointAsync(endpoint, ct);
        await audit.WriteAsync(new AuditEntry(AuditActions.ApiSubscriptionDeleted, "api_subscription", s.Id.ToString(),
            Changes: new { s.AppId, s.EventType, host = new Uri(s.Url).Host }), ct);
    }

    private Task<EventSubscriptionView?> GetAsync(Guid id, CancellationToken ct) =>
        session.QuerySingleOrDefaultAsync($"select {Columns} from publicapi.event_subscription s join publicapi.app a on a.id = s.app_id where s.id = @id",
            new { id }, Read, ct);

    private Task<Guid> EndpointAsync(Guid id, CancellationToken ct) =>
        session.ScalarAsync<Guid>("select endpoint_id from publicapi.event_subscription where id = @id", new { id }, ct);

    private async Task DemandAsync(string permission, CancellationToken ct)
    {
        await entitlements.DemandAsync(Capabilities.ApiPublic, ct);
        await authz.DemandAsync(permission, AuthzTarget.Tenant, ct);
    }
}

/// <summary>
/// Sends one public event to the subscriptions of its type (FR-018): only active subscriptions of active application
/// apps that hold <c>events:subscribe</c> and the resource's read scope, in a tenant whose public API is on and whose
/// package includes event subscriptions. Each app is asked, as itself, whether it may read the resource, through the
/// owning module's own read, in a scope that is never committed; an app that may not gets nothing, so a notification
/// never tells it that a hidden resource exists. The notification carries identifiers and a link only.
/// </summary>
public sealed class PublicEventFanout(
    IDbSession session,
    ITenantContext context,
    IEntitlementService entitlements,
    ApiSettings settings,
    IServiceScopeFactory scopes,
    IWebhookDelivery delivery,
    ITenantRegistry tenants,
    IOptions<HostResolutionOptions> hosts,
    IClock clock,
    ILogger<PublicEventFanout> logger)
{
    private sealed record Target(Guid Id, Guid AppId, Guid EndpointId);

    public async Task PublishAsync(string type, Guid resourceId, CancellationToken ct)
    {
        var definition = PublicEventCatalogue.Find(type) ?? throw new ArgumentException("Not a public event.", nameof(type));
        if (!await entitlements.IsEnabledAsync(Capabilities.ApiPublic, ct) || !await entitlements.IsEnabledAsync(Capabilities.ApiEventSubscriptions, ct)
            || !(await settings.CurrentAsync(ct)).ApiEnabled)
        {
            return;
        }
        var targets = await session.QueryAsync(
            """
            select s.id, s.app_id, s.endpoint_id
            from publicapi.event_subscription s
            join publicapi.app a on a.id = s.app_id
            left join publicapi.platform_block b on b.client_id = a.client_id
            where s.event_type = @type and s.status = 'active' and a.status = 'active' and a.kind = @kind and b.client_id is null
              and @subscribe = any(a.scopes) and @read = any(a.scopes)
            order by s.app_id, s.id
            """, new { type, kind = AppKinds.Application, subscribe = ScopeCatalogue.EventsSubscribe, read = definition.ReadScope },
            r => new Target(r.GetGuid(0), r.GetGuid(1), r.GetGuid(2)), ct);
        if (targets.Count == 0)
        {
            return;
        }
        var tenant = context.RequireTenant();
        var url = await OriginAsync(tenant, ct) + definition.PathFor(resourceId);
        var data = JsonSerializer.SerializeToElement(new PublicEventData(Ids.New(), definition.Version, definition.ResourceType, resourceId, url), JsonDefaults.Options);
        var at = clock.UtcNow;
        foreach (var app in targets.GroupBy(t => t.AppId))
        {
            if (!await VisibleAsync(tenant, app.Key, definition.ResourceType, resourceId, ct))
            {
                continue;
            }
            foreach (var target in app)
            {
                await delivery.DeliverAsync(target.EndpointId, definition.Type, at, data, ct);
            }
        }
    }

    private async Task<bool> VisibleAsync(Guid tenant, Guid appId, string resourceType, Guid resourceId, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var accessor = scope.ServiceProvider.GetRequiredService<TenantContextAccessor>();
        accessor.SetTenant(tenant);
        accessor.SetActor(ActorType.App, appId);
        var check = scope.ServiceProvider.GetServices<IPublicResourceVisibility>().FirstOrDefault(v => v.ResourceType == resourceType);
        if (check is null)
        {
            logger.LogWarning("No visibility check for public resource type {Type}", resourceType);
            return false;
        }
        // The read runs in its own transaction, which is never committed, so it leaves nothing behind.
        return await check.VisibleAsync(resourceId, ct);
    }

    private async Task<string> OriginAsync(Guid tenantId, CancellationToken ct)
    {
        var h = hosts.Value;
        if (h.Mode == InstallationMode.Dedicated)
        {
            return $"https://{h.BaseDomain}";
        }
        var tenant = await tenants.FindByIdAsync(tenantId, ct) ?? throw new InvalidOperationException("Unknown tenant.");
        return $"https://{tenant.Key}.{h.BaseDomain}";
    }
}

/// <summary>The internal events behind the public catalogue, each turned into its public event (section 12).</summary>
public sealed class PublicEventHandlers(PublicEventFanout fanout) :
    IMessageHandler<ContentPublished>,
    IMessageHandler<DocumentVersionPublished>,
    IMessageHandler<PolicyPublished>,
    IMessageHandler<RequestSubmitted>,
    IMessageHandler<RequestCompleted>,
    IMessageHandler<ProfileUpdated>,
    IMessageHandler<OrganizationUnitMoved>
{
    public Task HandleAsync(ContentPublished message, CancellationToken ct) => fanout.PublishAsync(PublicEventCatalogue.ContentPublished, message.ItemId, ct);

    public Task HandleAsync(DocumentVersionPublished message, CancellationToken ct) =>
        fanout.PublishAsync(PublicEventCatalogue.DocumentVersionPublished, message.DocumentId, ct);

    public Task HandleAsync(PolicyPublished message, CancellationToken ct) => fanout.PublishAsync(PublicEventCatalogue.PolicyPublished, message.PolicyId, ct);

    public Task HandleAsync(RequestSubmitted message, CancellationToken ct) => fanout.PublishAsync(PublicEventCatalogue.RequestSubmitted, message.RequestId, ct);

    public Task HandleAsync(RequestCompleted message, CancellationToken ct) => fanout.PublishAsync(PublicEventCatalogue.RequestCompleted, message.RequestId, ct);

    // The identifier only: which fields changed is not sent (section 12).
    public Task HandleAsync(ProfileUpdated message, CancellationToken ct) => fanout.PublishAsync(PublicEventCatalogue.ProfileUpdated, message.UserId, ct);

    public Task HandleAsync(OrganizationUnitMoved message, CancellationToken ct) => fanout.PublishAsync(PublicEventCatalogue.OrganizationUnitMoved, message.UnitId, ct);
}

/// <summary>
/// Pauses a subscription whose deliveries kept failing for 3 days (US4 scenario 4) and tells the API administrators. A
/// delivery that succeeds clears the failure. The webhook runtime reports each delivery's end for the endpoint.
/// </summary>
public sealed class SubscriptionHealth(
    IDbSession session,
    IWebhookDelivery delivery,
    IAuditWriter audit,
    IAuthorizationProvisioning roles,
    INotificationSender notifications,
    IClock clock) : IMessageHandler<ConnectorActionCompleted>
{
    public static readonly TimeSpan PauseAfter = TimeSpan.FromDays(3);
    public const string AdminTemplate = "api.subscription_paused.admin";

    public async Task HandleAsync(ConnectorActionCompleted message, CancellationToken ct)
    {
        var s = await session.QuerySingleOrDefaultAsync(
            """
            select s.id, s.app_id, a.name, s.event_type, s.url, s.status, s.failing_since
            from publicapi.event_subscription s join publicapi.app a on a.id = s.app_id where s.endpoint_id = @endpoint
            """, new { endpoint = message.ConnectionId },
            r => new
            {
                Id = r.GetGuid(0), AppId = r.GetGuid(1), App = r.GetString(2), Type = r.GetString(3), Url = r.GetString(4), Status = r.GetString(5),
                FailingSince = r.IsDBNull(6) ? (DateTimeOffset?)null : r.GetFieldValue<DateTimeOffset>(6),
            }, ct);
        if (s is null || s.Status != "active")
        {
            return;
        }
        var now = clock.UtcNow;
        if (message.Outcome == ConnectorOutcomes.Succeeded)
        {
            if (s.FailingSince is not null)
            {
                await session.ExecAsync("update publicapi.event_subscription set failing_since = null where id = @id", new { id = s.Id }, ct);
            }
            return;
        }
        if (message.Outcome is not (ConnectorOutcomes.Failed or ConnectorOutcomes.Unknown))
        {
            return;
        }
        if (s.FailingSince is not { } since)
        {
            await session.ExecAsync("update publicapi.event_subscription set failing_since = @now where id = @id", new { id = s.Id, now }, ct);
            return;
        }
        if (now - since < PauseAfter)
        {
            return;
        }
        await session.ExecAsync("update publicapi.event_subscription set status = 'paused', paused_at = @now where id = @id", new { id = s.Id, now }, ct);
        await delivery.SetEndpointEnabledAsync(message.ConnectionId, false, ct);
        var host = new Uri(s.Url).Host;
        await audit.WriteAsync(new AuditEntry(AuditActions.ApiSubscriptionPaused, "api_subscription", s.Id.ToString(),
            Changes: new { s.AppId, eventType = s.Type, host, failingSince = since, reason = "delivery_failed" }), ct);
        var admins = await roles.TenantHoldersAsync(Permissions.ApiManage, ct);
        if (admins.Count > 0)
        {
            await notifications.SendAsync(new NotificationRequest(admins, NotificationCategories.IntegrationAlert, AdminTemplate, $"/admin/api/apps/{s.AppId}",
                new Dictionary<string, string> { ["app"] = s.App, ["event"] = s.Type, ["host"] = host },
                DedupeKey: $"api_subscription_paused:{s.Id}:{now:yyyyMMddHHmm}", GroupKey: "api_subscription_paused:" + s.AppId), ct);
        }
    }
}
