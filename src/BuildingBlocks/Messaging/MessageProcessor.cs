using System.Diagnostics.Metrics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TeamBeats.Persistence;
using TeamBeats.Primitives;
using TeamBeats.TenantContext;

namespace TeamBeats.Messaging;

public enum ProcessResult
{
    Done,
    Duplicate,
    Deferred,
    Dropped,
    Retry,
    DeadLettered,
}

public sealed record ConsumerRegistration(string Name, string MessageType, Type EventType, Type HandlerType);

/// <summary>
/// How soon the consumers whose names start with <see cref="Prefix"/> should handle a message after it happened, such as
/// search indexing within 5 minutes (spec 012 FR-006). A message handled later counts on <c>messaging.consumer.lag_exceeded</c>
/// and is logged, which is what operators alert on.
/// </summary>
public sealed record ConsumerLagObjective(string Prefix, TimeSpan Objective);

/// <summary>
/// Shared consumer pipeline: tenant header check, tenant status gate (spec 001 T031), inbox de-duplication,
/// transaction per message with tenant context, retries, and dead-lettering (constitution XVIII).
/// </summary>
public sealed class MessageProcessor(
    IServiceScopeFactory scopes,
    IEnumerable<ConsumerRegistration> registrations,
    IEnumerable<ConsumerLagObjective> objectives,
    ILogger<MessageProcessor> logger)
{
    public const int MaxAttempts = 5;
    private readonly List<ConsumerRegistration> _registrations = registrations.ToList();
    private readonly List<ConsumerLagObjective> _objectives = objectives.OrderByDescending(o => o.Prefix.Length).ToList();

    private static readonly Meter Meter = new("TeamBeats.Messaging");

    /// <summary>Seconds from when a message happened to when its consumer committed, per consumer (spec 012 FR-006 lag metric).</summary>
    private static readonly Histogram<double> Lag = Meter.CreateHistogram<double>("messaging.consumer.lag", "s");

    private static readonly Counter<long> LagExceeded = Meter.CreateCounter<long>("messaging.consumer.lag_exceeded");

    /// <summary>The objective that applies to a consumer: the one with the longest matching prefix, if any.</summary>
    public TimeSpan? ObjectiveFor(string consumer) => _objectives.FirstOrDefault(o => consumer.StartsWith(o.Prefix, StringComparison.Ordinal))?.Objective;

    private void RecordLag(ConsumerRegistration consumer, Envelope envelope)
    {
        var lag = DateTimeOffset.UtcNow - envelope.OccurredAt;
        var tag = new KeyValuePair<string, object?>("consumer", consumer.Name);
        Lag.Record(Math.Max(0, lag.TotalSeconds), tag);
        if (ObjectiveFor(consumer.Name) is { } objective && lag > objective)
        {
            LagExceeded.Add(1, tag);
            logger.LogWarning("Consumer {Consumer} handled {MessageId} for tenant {TenantId} {Lag} after it happened, over its objective of {Objective}",
                consumer.Name, envelope.MessageId, envelope.TenantId, lag, objective);
        }
    }

    public IEnumerable<ConsumerRegistration> Consumers => _registrations;

    public IEnumerable<ConsumerRegistration> ConsumersFor(string messageType) =>
        _registrations.Where(r => r.MessageType == messageType);

    public async Task<ProcessResult> ProcessAsync(ConsumerRegistration consumer, Envelope envelope, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var previousCorrelation = Correlation.Id;
        Correlation.Id = envelope.CorrelationId ?? Correlation.NewId();

        try
        {
            if (envelope.TenantId is null && !Envelope.IsPlatformMessage(consumer.EventType))
            {
                await DeadLetterAsync(services, consumer, envelope, "missing tenant header", ct);
                return ProcessResult.DeadLettered;
            }

            var handler = services.GetRequiredService(consumer.HandlerType);
            var policy = GetPolicy(handler, consumer.EventType);

            if (envelope.TenantId is { } tenantId)
            {
                var registry = services.GetRequiredService<ITenantRegistry>();
                var tenant = await registry.FindByIdAsync(tenantId, ct);
                if (tenant is null)
                {
                    await DeadLetterAsync(services, consumer, envelope, "unknown tenant", ct);
                    return ProcessResult.DeadLettered;
                }

                if (tenant.Status != TenantLifecycleStatus.Active && policy != InactiveTenantPolicy.Process)
                {
                    logger.LogInformation("Message {MessageId} for inactive tenant {TenantId}: {Policy}", envelope.MessageId, tenantId, policy);
                    return policy == InactiveTenantPolicy.Drop ? ProcessResult.Dropped : ProcessResult.Deferred;
                }

                var ctx = services.GetRequiredService<TenantContextAccessor>();
                ctx.SetTenant(tenantId);
                ctx.SetActor(ActorType.System, null);
            }

            var session = services.GetRequiredService<IDbSession>();
            var inserted = await session.ExecAsync(
                "insert into messaging.inbox (consumer, message_id, tenant_id) values (@c, @m, @t) on conflict do nothing",
                new { c = consumer.Name, m = envelope.MessageId, t = envelope.TenantId }, ct);
            if (inserted == 0)
            {
                return ProcessResult.Duplicate;
            }

            var message = typeof(Envelope).GetMethod(nameof(Envelope.Deserialize))!
                .MakeGenericMethod(consumer.EventType).Invoke(envelope, null)!;
            var handleMethod = typeof(IMessageHandler<>).MakeGenericType(consumer.EventType).GetMethod("HandleAsync")!;
            await (Task)handleMethod.Invoke(handler, [message, ct])!;
            await session.CommitAsync(ct);
            RecordLag(consumer, envelope);
            return ProcessResult.Done;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var root = ex is System.Reflection.TargetInvocationException { InnerException: { } inner } ? inner : ex;
            logger.LogWarning(root, "Consumer {Consumer} failed on {MessageId} attempt {Attempt}", consumer.Name, envelope.MessageId, envelope.Attempt);
            if (envelope.Attempt + 1 >= MaxAttempts)
            {
                await using var dlScope = scopes.CreateAsyncScope();
                await DeadLetterAsync(dlScope.ServiceProvider, consumer, envelope, root.GetType().Name + ": " + root.Message, ct);
                return ProcessResult.DeadLettered;
            }
            return ProcessResult.Retry;
        }
        finally
        {
            Correlation.Id = previousCorrelation;
        }
    }

    private static InactiveTenantPolicy GetPolicy(object handler, Type eventType)
    {
        var iface = typeof(IMessageHandler<>).MakeGenericType(eventType);
        var map = handler.GetType().GetInterfaceMap(iface);
        var getter = iface.GetProperty(nameof(IMessageHandler<IIntegrationEvent>.InactiveTenantPolicy))!.GetMethod!;
        var index = Array.IndexOf(map.InterfaceMethods, getter);
        if (index >= 0)
        {
            return (InactiveTenantPolicy)map.TargetMethods[index].Invoke(handler, null)!;
        }
        return InactiveTenantPolicy.Defer;
    }

    private static async Task DeadLetterAsync(IServiceProvider services, ConsumerRegistration consumer, Envelope envelope, string reason, CancellationToken ct)
    {
        var data = services.GetRequiredService<AppDataSource>().DataSource;
        await using var cmd = data.CreateCommand(
            """
            insert into messaging.dead_letter (consumer, message_id, message_type, tenant_id, correlation_id, payload, reason)
            values (@c, @m, @t, @tenant, @corr, @p::jsonb, @r)
            """);
        cmd.Parameters.AddWithValue("c", consumer.Name);
        cmd.Parameters.AddWithValue("m", envelope.MessageId);
        cmd.Parameters.AddWithValue("t", envelope.MessageType);
        cmd.Parameters.AddWithValue("tenant", (object?)envelope.TenantId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("corr", (object?)envelope.CorrelationId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("p", envelope.Payload);
        cmd.Parameters.AddWithValue("r", reason);
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
