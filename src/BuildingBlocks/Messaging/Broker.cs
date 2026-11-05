namespace TeamBeats.Messaging;

/// <summary>Transport abstraction. RabbitMQ in deployments, in-memory for tests and broker-less dev runs.</summary>
public interface IMessageBroker
{
    Task PublishAsync(Envelope envelope, CancellationToken ct);
}

/// <summary>Delivers each published message straight to the processor for every subscribed handler.</summary>
public sealed class InMemoryBroker(MessageProcessor processor) : IMessageBroker
{
    public async Task PublishAsync(Envelope envelope, CancellationToken ct)
    {
        foreach (var consumer in processor.ConsumersFor(envelope.MessageType))
        {
            var current = envelope;
            while (true)
            {
                var result = await processor.ProcessAsync(consumer, current, ct);
                if (result != ProcessResult.Retry)
                {
                    break;
                }
                current = current with { Attempt = current.Attempt + 1 };
            }
        }
    }
}
