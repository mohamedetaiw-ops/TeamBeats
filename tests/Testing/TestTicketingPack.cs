using System.Collections.Concurrent;
using System.Text.Json;
using TeamBeats.Entitlements.Contracts;
using TeamBeats.Modules.Integrations.Contracts;
using TeamBeats.Modules.Integrations.Runtime;

namespace TeamBeats.Testing;

/// <summary>
/// A ticketing system the tests control (spec 017 plan: fake external systems with faults). Each connection has a
/// script of answers, taken one per attempt; when it runs out the system creates an incident. Every attempt is recorded.
/// </summary>
public sealed class FakeTicketing
{
    public sealed record Received(Guid ConnectionId, string Action, string IdempotencyKey, int Attempt, string Input);

    private readonly ConcurrentDictionary<Guid, ConcurrentQueue<Func<PackCall, CancellationToken, Task<PackCallResult>>>> scripts = new();
    private readonly ConcurrentQueue<Received> calls = new();
    private int created;

    public void Script(Guid connectionId, params Func<PackCall, CancellationToken, Task<PackCallResult>>[] answers)
    {
        var queue = scripts.GetOrAdd(connectionId, _ => new());
        foreach (var a in answers)
        {
            queue.Enqueue(a);
        }
    }

    public void Script(Guid connectionId, params PackCallResult[] answers) =>
        Script(connectionId, answers.Select(a => (Func<PackCall, CancellationToken, Task<PackCallResult>>)((_, _) => Task.FromResult(a))).ToArray());

    /// <summary>An answer that never comes, so the runtime's timeout ends the attempt.</summary>
    public static Func<PackCall, CancellationToken, Task<PackCallResult>> Hang => async (_, ct) =>
    {
        await Task.Delay(Timeout.Infinite, ct);
        return PackCallResult.Ok();
    };

    public IReadOnlyList<Received> CallsFor(Guid connectionId) => calls.Where(c => c.ConnectionId == connectionId).ToList();

    public async Task<PackCallResult> HandleAsync(PackCall call, CancellationToken ct)
    {
        calls.Enqueue(new Received(call.Connection.Id, call.Action, call.IdempotencyKey, call.Attempt, call.Input.GetRawText()));
        if (scripts.TryGetValue(call.Connection.Id, out var queue) && queue.TryDequeue(out var answer))
        {
            return await answer(call, ct);
        }
        var n = Interlocked.Increment(ref created);
        return PackCallResult.Ok(JsonSerializer.SerializeToElement(new { number = $"INC{n:D6}", state = "new" }), $"INC{n:D6}");
    }
}

public sealed class TestTicketingPack(FakeTicketing fake) : IConnectorPack
{
    public const string KindName = "test_ticketing";

    public string Kind => KindName;

    public int Version => 1;

    public string CapabilityKey => Capabilities.IntegrationConnectors;

    public IReadOnlyList<ConnectorActionDefinition> Actions { get; } =
    [
        new("create_incident", ConnectorSideEffects.Write, Idempotent: false, ["invalid_category"]),
        new("create_incident_keyed", ConnectorSideEffects.Write, Idempotent: true, ["invalid_category"]),
        new("get_status", ConnectorSideEffects.Read, Idempotent: true, []),
    ];

    public Task<PackCallResult> CallAsync(PackCall call, CancellationToken ct) => fake.HandleAsync(call, ct);
}
