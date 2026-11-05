using TeamBeats.Primitives;
namespace TeamBeats.Messaging;

/// <summary>What a consumer does with messages for a tenant that is not Active (spec 001 US3 scenario 3).</summary>
public enum InactiveTenantPolicy
{
    /// <summary>Do not process now; redeliver later (paused jobs resume on reactivation).</summary>
    Defer,

    /// <summary>Drop the message.</summary>
    Drop,

    /// <summary>Process regardless, for lifecycle handlers such as revoking sessions on suspension.</summary>
    Process,
}

public interface IMessageHandler<in T> where T : IIntegrationEvent
{
    InactiveTenantPolicy InactiveTenantPolicy => InactiveTenantPolicy.Defer;

    Task HandleAsync(T message, CancellationToken ct);
}
