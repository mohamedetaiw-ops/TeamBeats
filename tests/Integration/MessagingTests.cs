using Microsoft.Extensions.DependencyInjection;
using TeamBeats.Messaging;
using TeamBeats.Testing;
using Xunit;

namespace TeamBeats.Integration.Tests;

/// <summary>ADR-0006 outbox and consumer behaviour: tenant header, dedupe, retry, dead letter, inactive tenants.</summary>
[Collection(WorldCollection.Name)]
public sealed class MessagingTests(TenantWorld world)
{
    private (MessageProcessor Processor, ConsumerRegistration Consumer) ExportConsumer()
    {
        var processor = world.Factory.Services.GetRequiredService<MessageProcessor>();
        return (processor, processor.ConsumersFor("AuditExportRequested").Single());
    }

    private static Envelope Export(Guid? tenant, string? payload = null, int attempt = 0) =>
        new(Guid.NewGuid(), "AuditExportRequested", tenant, "corr-test", DateTimeOffset.UtcNow,
            payload ?? $"{{\"jobId\":\"{Guid.NewGuid()}\"}}", attempt);

    private Task<long> DeadLetters(Guid messageId) =>
        world.Database.OwnerScalarAsync<long>("select count(*) from messaging.dead_letter where message_id = @m", ("m", messageId));

    [Fact]
    public async Task Tenant_message_without_tenant_header_is_dead_lettered()
    {
        var (processor, consumer) = ExportConsumer();
        var envelope = Export(null);
        Assert.Equal(ProcessResult.DeadLettered, await processor.ProcessAsync(consumer, envelope, CancellationToken.None));
        Assert.Equal(1, await DeadLetters(envelope.MessageId));
    }

    [Fact]
    public async Task Unknown_tenant_is_dead_lettered()
    {
        var (processor, consumer) = ExportConsumer();
        var envelope = Export(Guid.NewGuid());
        Assert.Equal(ProcessResult.DeadLettered, await processor.ProcessAsync(consumer, envelope, CancellationToken.None));
    }

    [Fact]
    public async Task Duplicate_delivery_is_processed_once()
    {
        var (processor, consumer) = ExportConsumer();
        var envelope = Export(world.A.Id);
        Assert.Equal(ProcessResult.Done, await processor.ProcessAsync(consumer, envelope, CancellationToken.None));
        Assert.Equal(ProcessResult.Duplicate, await processor.ProcessAsync(consumer, envelope, CancellationToken.None));
    }

    [Fact]
    public async Task Failure_retries_then_dead_letters_on_last_attempt()
    {
        var (processor, consumer) = ExportConsumer();
        var first = Export(world.A.Id, payload: "null");
        Assert.Equal(ProcessResult.Retry, await processor.ProcessAsync(consumer, first, CancellationToken.None));

        var last = Export(world.A.Id, payload: "null", attempt: MessageProcessor.MaxAttempts - 1);
        Assert.Equal(ProcessResult.DeadLettered, await processor.ProcessAsync(consumer, last, CancellationToken.None));
        Assert.Equal(1, await DeadLetters(last.MessageId));
    }

    [Fact]
    public async Task Messages_for_suspended_tenant_are_deferred_by_default()
    {
        var tenant = await world.CreateTenantAsync("msg-susp", "workplace");
        (await world.Platform.PostAsync($"/api/v1/platform/tenants/{tenant.Id}:suspend", null)).EnsureSuccessStatusCode();
        var (processor, consumer) = ExportConsumer();
        Assert.Equal(ProcessResult.Deferred, await processor.ProcessAsync(consumer, Export(tenant.Id), CancellationToken.None));
    }
}
