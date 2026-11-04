using System.Text.Json;
using TeamBeats.Persistence;
using TeamBeats.Primitives;
using TeamBeats.TenantContext;

namespace TeamBeats.Messaging;

public interface IOutbox
{
    /// <summary>Stores the event in the current transaction. It is published only if the transaction commits.</summary>
    Task EnqueueAsync<T>(T @event, CancellationToken ct = default) where T : IIntegrationEvent;
}

public sealed class Outbox(IDbSession session, ITenantContext context, IClock clock) : IOutbox
{
    public async Task EnqueueAsync<T>(T @event, CancellationToken ct = default) where T : IIntegrationEvent
    {
        var tenantId = context.TenantId;
        if (tenantId is null && !Envelope.IsPlatformMessage(typeof(T)))
        {
            throw new InvalidOperationException($"{typeof(T).Name} requires a tenant context.");
        }

        await session.ExecAsync(
            """
            insert into messaging.outbox (message_id, message_type, tenant_id, correlation_id, occurred_at, payload)
            values (@id, @type, @tenant, @corr, @at, @payload)
            """,
            new
            {
                id = Ids.New(),
                type = Envelope.TypeName(typeof(T)),
                tenant = tenantId,
                corr = Correlation.Id,
                at = clock.UtcNow,
                payload = new Jsonb(JsonSerializer.Serialize(@event, JsonDefaults.Options)),
            },
            ct);
    }
}
