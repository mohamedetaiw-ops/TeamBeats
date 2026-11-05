using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TeamBeats.Persistence;

namespace TeamBeats.Messaging;

/// <summary>
/// Publishes committed outbox rows to the broker in order. Rows are claimed with SKIP LOCKED so several
/// worker instances can run. A crash between publish and marking produces a duplicate, which consumers
/// absorb through the inbox (at-least-once delivery).
/// </summary>
public sealed class OutboxDispatcher(AppDataSource dataSource, IMessageBroker broker, ILogger<OutboxDispatcher> logger)
{
    public async Task<int> DispatchBatchAsync(int batchSize, CancellationToken ct)
    {
        await using var conn = await dataSource.DataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var batch = new List<Envelope>();
        await using (var cmd = new Npgsql.NpgsqlCommand(
            """
            select message_id, message_type, tenant_id, correlation_id, occurred_at, payload::text as payload
            from messaging.outbox
            where dispatched_at is null
            order by seq
            limit @n
            for update skip locked
            """, conn, tx))
        {
            cmd.Parameters.AddWithValue("n", batchSize);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                batch.Add(new Envelope(
                    r.GetGuid(0),
                    r.GetString(1),
                    r.IsDBNull(2) ? null : r.GetGuid(2),
                    r.IsDBNull(3) ? null : r.GetString(3),
                    r.GetFieldValue<DateTimeOffset>(4),
                    r.GetString(5)));
            }
        }

        var sent = 0;
        foreach (var envelope in batch)
        {
            try
            {
                await broker.PublishAsync(envelope, ct);
                await using var mark = new Npgsql.NpgsqlCommand(
                    "update messaging.outbox set dispatched_at = now(), attempts = attempts + 1 where message_id = @id", conn, tx);
                mark.Parameters.AddWithValue("id", envelope.MessageId);
                await mark.ExecuteNonQueryAsync(ct);
                sent++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Publishing {MessageId} failed", envelope.MessageId);
                await using var fail = new Npgsql.NpgsqlCommand(
                    "update messaging.outbox set attempts = attempts + 1, last_error = @e where message_id = @id", conn, tx);
                fail.Parameters.AddWithValue("id", envelope.MessageId);
                fail.Parameters.AddWithValue("e", ex.Message);
                await fail.ExecuteNonQueryAsync(ct);
                break;
            }
        }

        await tx.CommitAsync(ct);
        return sent;
    }
}

public sealed class OutboxDispatcherService(OutboxDispatcher dispatcher, ILogger<OutboxDispatcherService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            int sent;
            try
            {
                sent = await dispatcher.DispatchBatchAsync(100, stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Outbox dispatch failed");
                sent = 0;
            }

            if (sent == 0)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(500), stoppingToken);
            }
        }
    }
}
