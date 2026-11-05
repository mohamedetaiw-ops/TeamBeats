using System.Text;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace TeamBeats.Messaging;

public sealed class RabbitMqOptions
{
    public const string Section = "Messaging:RabbitMq";

    public string Uri { get; set; } = "amqp://localhost:5672";
    public string Exchange { get; set; } = "teambeats.events";
    public TimeSpan DeferDelay { get; set; } = TimeSpan.FromSeconds(30);
}

/// <summary>
/// RabbitMQ transport on <c>RabbitMQ.Client</c> (ADR-0006). Topic exchange keyed by message type, one durable
/// queue per consumer, a dead-letter queue per consumer. Retries and deferrals are republished with an
/// incremented attempt header, so poison messages cannot loop forever.
/// </summary>
public sealed class RabbitMqConnection(IOptions<RabbitMqOptions> options) : IAsyncDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private IConnection? _connection;

    public RabbitMqOptions Options => options.Value;

    public async Task<IConnection> GetAsync(CancellationToken ct)
    {
        if (_connection is { IsOpen: true })
        {
            return _connection;
        }

        await _gate.WaitAsync(ct);
        try
        {
            if (_connection is not { IsOpen: true })
            {
                var factory = new ConnectionFactory { Uri = new Uri(options.Value.Uri), ClientProvidedName = "teambeats" };
                _connection = await factory.CreateConnectionAsync(ct);
            }
            return _connection;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_connection is not null)
        {
            await _connection.DisposeAsync();
        }
        _gate.Dispose();
    }
}

public sealed class RabbitMqBroker(RabbitMqConnection connection) : IMessageBroker
{
    public async Task PublishAsync(Envelope envelope, CancellationToken ct)
    {
        var conn = await connection.GetAsync(ct);
        await using var channel = await conn.CreateChannelAsync(new CreateChannelOptions(publisherConfirmationsEnabled: true, publisherConfirmationTrackingEnabled: true), ct);
        await channel.ExchangeDeclareAsync(connection.Options.Exchange, ExchangeType.Topic, durable: true, cancellationToken: ct);
        await channel.BasicPublishAsync(connection.Options.Exchange, envelope.MessageType, mandatory: false,
            RabbitMqEnvelope.Properties(envelope), Encoding.UTF8.GetBytes(envelope.Payload), ct);
    }
}

internal static class RabbitMqEnvelope
{
    public static BasicProperties Properties(Envelope e) => new()
    {
        MessageId = e.MessageId.ToString(),
        Type = e.MessageType,
        CorrelationId = e.CorrelationId,
        Persistent = true,
        ContentType = "application/json",
        Timestamp = new AmqpTimestamp(e.OccurredAt.ToUnixTimeSeconds()),
        Headers = new Dictionary<string, object?>
        {
            ["tenant_id"] = e.TenantId?.ToString(),
            ["correlation_id"] = e.CorrelationId,
            ["message_id"] = e.MessageId.ToString(),
            ["occurred_at"] = e.OccurredAt.ToString("O"),
            ["attempt"] = e.Attempt,
        },
    };

    public static Envelope Read(BasicDeliverEventArgs args)
    {
        var h = new Dictionary<string, object?>(args.BasicProperties.Headers ?? new Dictionary<string, object?>());
        static string? Str(object? v) => v switch
        {
            null => null,
            byte[] b => Encoding.UTF8.GetString(b),
            _ => v.ToString(),
        };

        var tenant = Str(h.GetValueOrDefault("tenant_id"));
        var attempt = h.GetValueOrDefault("attempt") switch
        {
            int i => i,
            long l => (int)l,
            _ => 0,
        };
        return new Envelope(
            Guid.Parse(args.BasicProperties.MessageId!),
            args.BasicProperties.Type ?? args.RoutingKey,
            string.IsNullOrEmpty(tenant) ? null : Guid.Parse(tenant),
            Str(h.GetValueOrDefault("correlation_id")),
            DateTimeOffset.TryParse(Str(h.GetValueOrDefault("occurred_at")), out var at) ? at : DateTimeOffset.UtcNow,
            Encoding.UTF8.GetString(args.Body.Span),
            attempt);
    }
}

/// <summary>Hosts one RabbitMQ consumer per registered handler in the worker process.</summary>
public sealed class RabbitMqConsumerService(
    RabbitMqConnection connection,
    MessageProcessor processor,
    ILogger<RabbitMqConsumerService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var conn = await connection.GetAsync(stoppingToken);
        var exchange = connection.Options.Exchange;
        var channels = new List<IChannel>();
        foreach (var consumer in processor.Consumers)
        {
            var channel = await conn.CreateChannelAsync(cancellationToken: stoppingToken);
            channels.Add(channel);
            await channel.ExchangeDeclareAsync(exchange, ExchangeType.Topic, durable: true, cancellationToken: stoppingToken);
            var queue = "teambeats." + consumer.Name;
            await channel.QueueDeclareAsync(queue + ".dlq", durable: true, exclusive: false, autoDelete: false, cancellationToken: stoppingToken);
            await channel.QueueDeclareAsync(queue, durable: true, exclusive: false, autoDelete: false, cancellationToken: stoppingToken);
            await channel.QueueBindAsync(queue, exchange, consumer.MessageType, cancellationToken: stoppingToken);
            await channel.BasicQosAsync(0, 10, false, stoppingToken);

            var registration = consumer;
            var asyncConsumer = new AsyncEventingBasicConsumer(channel);
            asyncConsumer.ReceivedAsync += async (_, args) =>
            {
                var envelope = RabbitMqEnvelope.Read(args);
                var result = await processor.ProcessAsync(registration, envelope, stoppingToken);
                switch (result)
                {
                    case ProcessResult.Retry:
                    case ProcessResult.Deferred:
                        if (result == ProcessResult.Deferred)
                        {
                            await Task.Delay(connection.Options.DeferDelay, stoppingToken);
                        }
                        var next = result == ProcessResult.Retry ? envelope with { Attempt = envelope.Attempt + 1 } : envelope;
                        await channel.BasicPublishAsync(string.Empty, queue, false, RabbitMqEnvelope.Properties(next),
                            Encoding.UTF8.GetBytes(next.Payload), stoppingToken);
                        break;
                    case ProcessResult.DeadLettered:
                        await channel.BasicPublishAsync(string.Empty, queue + ".dlq", false, RabbitMqEnvelope.Properties(envelope),
                            Encoding.UTF8.GetBytes(envelope.Payload), stoppingToken);
                        break;
                }
                await channel.BasicAckAsync(args.DeliveryTag, false, stoppingToken);
            };
            await channel.BasicConsumeAsync(queue, autoAck: false, consumer: asyncConsumer, cancellationToken: stoppingToken);
            logger.LogInformation("Consuming {Queue} for {MessageType}", queue, consumer.MessageType);
        }

        try
        {
            await Task.Delay(Timeout.Infinite, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            // Shutting down.
        }

        foreach (var channel in channels)
        {
            await channel.CloseAsync();
            await channel.DisposeAsync();
        }
    }
}
