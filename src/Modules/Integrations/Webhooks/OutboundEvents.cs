using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TeamBeats.Entitlements.Contracts;
using TeamBeats.Messaging;
using TeamBeats.Modules.Content.Contracts;
using TeamBeats.Modules.Integrations.Contracts;
using TeamBeats.Modules.Services.Contracts;
using TeamBeats.Modules.Workflow.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Primitives;

namespace TeamBeats.Modules.Integrations.Webhooks;

/// <summary>
/// The product events an outbound webhook may send (spec 017 US5) and the fields of each a receiver may get. Events
/// carry identifiers and states only; the receiver reads more through the public API with its own access (spec 035).
/// Fields that name a person, such as who completed a task, are not offered.
/// </summary>
public static class OutboundEvents
{
    private static readonly Dictionary<string, string[]> Catalogue = new(StringComparer.Ordinal)
    {
        [nameof(RequestSubmitted)] = ["requestId", "serviceId", "number"],
        [nameof(RequestCompleted)] = ["requestId", "serviceId", "status"],
        [nameof(RequestReturned)] = ["requestId", "serviceId"],
        [nameof(ContentPublished)] = ["itemId", "siteId", "type", "version"],
        [nameof(UserTaskCreated)] = ["taskId", "instanceId", "definitionKey", "businessKey", "elementId", "dueAt"],
        [nameof(UserTaskCompleted)] = ["taskId", "instanceId", "definitionKey", "businessKey", "elementId", "outcome"],
        [nameof(ProcessInstanceCompleted)] = ["instanceId", "definitionKey", "businessKey", "outcome"],
    };

    public static IReadOnlyDictionary<string, string[]> All => Catalogue;

    public static IReadOnlyCollection<string>? FieldsOf(string type) => Catalogue.TryGetValue(type, out var f) ? f : null;

    public static IServiceCollection AddOutboundWebhookEvents(this IServiceCollection services)
    {
        services.AddMessageHandler<RequestSubmitted, WebhookFanout<RequestSubmitted>>("integrations.webhooks.request_submitted");
        services.AddMessageHandler<RequestCompleted, WebhookFanout<RequestCompleted>>("integrations.webhooks.request_completed");
        services.AddMessageHandler<RequestReturned, WebhookFanout<RequestReturned>>("integrations.webhooks.request_returned");
        services.AddMessageHandler<ContentPublished, WebhookFanout<ContentPublished>>("integrations.webhooks.content_published");
        services.AddMessageHandler<UserTaskCreated, WebhookFanout<UserTaskCreated>>("integrations.webhooks.user_task_created");
        services.AddMessageHandler<UserTaskCompleted, WebhookFanout<UserTaskCompleted>>("integrations.webhooks.user_task_completed");
        services.AddMessageHandler<ProcessInstanceCompleted, WebhookFanout<ProcessInstanceCompleted>>("integrations.webhooks.process_completed");
        return services;
    }
}

/// <summary>
/// Sends one product event to every enabled webhook connection that selected it (FR-013): a delivery job per
/// connection, with only the selected fields, through the connector runtime. Each delivery gets a new delivery ID,
/// which is its idempotency key; the message pipeline hands this handler each message once per consumer. Connections
/// that are turned off, and tenants whose package does not include webhooks, get nothing: events are not stored for later.
/// </summary>
public sealed class WebhookFanout<T>(IDbSession session, IEntitlementService entitlements, IConnectorRuntime runtime, IClock clock) : IMessageHandler<T>
    where T : IIntegrationEvent
{
    public async Task HandleAsync(T message, CancellationToken ct)
    {
        var type = typeof(T).Name;
        if (OutboundEvents.FieldsOf(type) is not { } allowed
            || !await entitlements.IsEnabledAsync(Capabilities.IntegrationConnectors, ct) || !await entitlements.IsEnabledAsync(Capabilities.IntegrationWebhooks, ct))
        {
            return;
        }
        var targets = await session.QueryAsync(
            """
            select id, settings::text from integrations.connection
            where kind = @kind and enabled and settings -> 'events' @> @filter
            order by id
            """, new { kind = WebhookPack.KindName, filter = Jsonb.From(new[] { new { type } }) },
            r => (Id: r.GetGuid(0), Settings: r.GetString(1)), ct);
        if (targets.Count == 0)
        {
            return;
        }
        var all = JsonSerializer.SerializeToElement(message, JsonDefaults.Options);
        foreach (var (id, settings) in targets)
        {
            var selected = WebhookPack.Parse(JsonDocument.Parse(settings).RootElement)?.Events?.FirstOrDefault(e => e.Type == type)?.Fields ?? [];
            var data = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            foreach (var field in selected.Where(allowed.Contains))
            {
                if (all.TryGetProperty(field, out var value))
                {
                    data[field] = value.Clone();
                }
            }
            var input = JsonSerializer.SerializeToElement(new { type, occurredAt = clock.UtcNow, data }, JsonDefaults.Options);
            await runtime.EnqueueAsync(new ConnectorCall(id, WebhookPack.DeliverAction, input, "wh_" + Ids.New().ToString("N"), type), ct);
        }
    }
}
