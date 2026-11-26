using System.Globalization;
using System.Text.Json;
using TeamBeats.Entitlements.Contracts;
using TeamBeats.Modules.Integrations.Contracts;
using TeamBeats.Modules.Integrations.Runtime;
using TeamBeats.Persistence;

namespace TeamBeats.Modules.Integrations.Ticketing;

/// <summary>
/// The task tracker contract over the Planner and Jira task packs (spec 029 FR-023 to FR-025). Every call runs inline
/// through the connector runtime (its retries, circuit and run history), and only enabled connections in the package are
/// used. Answers are canonical: nothing of Planner or Jira leaves this class.
/// </summary>
public sealed class TaskTrackers(IDbSession session, IConnectorRuntime runtime, IEntitlementService entitlements, TeamBeats.Primitives.IClock clock) : ITaskTrackers
{
    public async Task<IReadOnlyList<TaskTrackerConnection>> ConnectionsAsync(CancellationToken ct = default)
    {
        if (!await entitlements.IsEnabledAsync(Capabilities.IntegrationConnectors, ct))
        {
            return [];
        }
        return await session.QueryAsync(
            "select id, kind, display_name from integrations.connection where kind = any(@kinds) and enabled order by display_name, id",
            new { kinds = TaskTrackerKinds.All.ToArray() }, r => new TaskTrackerConnection(r.GetGuid(0), r.GetString(1), r.GetString(2)), ct);
    }

    private async Task<bool> UsableAsync(Guid connectionId, CancellationToken ct) =>
        await entitlements.IsEnabledAsync(Capabilities.IntegrationConnectors, ct)
        && await session.ScalarAsync<bool>("select exists(select 1 from integrations.connection where id = @connectionId and kind = any(@kinds) and enabled)",
            new { connectionId, kinds = TaskTrackerKinds.All.ToArray() }, ct);

    private static string Failed(ConnectorCallResult result) =>
        result.ErrorCode == TicketingPack.NotFound ? TrackerResults.NotFound
        : result.Outcome == ConnectorOutcomes.Refused || result.ErrorClass is ConnectorErrorClasses.InvalidInput or ConnectorErrorClasses.BusinessError ? TrackerResults.Refused
        : TrackerResults.Unavailable;

    public async Task<TrackerRead<TrackerContainer>> ContainersAsync(Guid connectionId, CancellationToken ct = default)
    {
        if (!await UsableAsync(connectionId, ct))
        {
            return new(TrackerResults.NotConfigured, []);
        }
        var result = await runtime.InvokeAsync(new ConnectorCall(connectionId, TaskTrackerPack.ListContainers, JsonSerializer.SerializeToElement(new { }),
            $"tracker-containers:{TeamBeats.Primitives.Ids.New():N}"), ct);
        if (result.Outcome != ConnectorOutcomes.Succeeded || result.Output?.TryGetProperty("containers", out var list) != true)
        {
            return new(Failed(result), []);
        }
        return new(TrackerResults.Ok, list.EnumerateArray().Select(c => new TrackerContainer(c.GetProperty("id").GetString()!, Optional(c, "name") ?? c.GetProperty("id").GetString()!)).ToList());
    }

    public async Task<TrackerRead<TrackerTask>> TasksAsync(Guid connectionId, string containerId, CancellationToken ct = default)
    {
        if (!await UsableAsync(connectionId, ct))
        {
            return new(TrackerResults.NotConfigured, []);
        }
        var result = await runtime.InvokeAsync(new ConnectorCall(connectionId, TaskTrackerPack.ListTasks, JsonSerializer.SerializeToElement(new { container = containerId }),
            $"tracker-tasks:{TeamBeats.Primitives.Ids.New():N}"), ct);
        if (result.Outcome != ConnectorOutcomes.Succeeded || result.Output?.TryGetProperty("tasks", out var list) != true)
        {
            return new(Failed(result), []);
        }
        return new(TrackerResults.Ok, list.EnumerateArray().Select(Read).ToList());
    }

    public Task<TrackerWrite> CreateAsync(Guid connectionId, string containerId, TrackerTaskInput task, string idempotencyKey, CancellationToken ct = default) =>
        WriteAsync(connectionId, TaskTrackerPack.CreateTask, containerId, null, task, idempotencyKey, ct);

    public Task<TrackerWrite> UpdateAsync(Guid connectionId, string containerId, string externalId, TrackerTaskInput task, string idempotencyKey, CancellationToken ct = default) =>
        WriteAsync(connectionId, TaskTrackerPack.UpdateTask, containerId, externalId, task, idempotencyKey, ct);

    private async Task<TrackerWrite> WriteAsync(Guid connectionId, string action, string containerId, string? externalId, TrackerTaskInput task, string key, CancellationToken ct)
    {
        if (!await UsableAsync(connectionId, ct))
        {
            return new(TrackerResults.NotConfigured, null);
        }
        var input = JsonSerializer.SerializeToElement(new
        {
            container = containerId,
            external_id = externalId,
            title = task.Title,
            description = task.Description,
            due_on = task.DueOn?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            done = task.Done,
            assignees = task.AssigneeEmails,
        });
        var result = await runtime.InvokeAsync(new ConnectorCall(connectionId, action, input, key), ct);
        if (result.Outcome != ConnectorOutcomes.Succeeded || result.Output?.TryGetProperty("task", out var written) != true)
        {
            return new(Failed(result), null);
        }
        return new(TrackerResults.Ok, Read(written));
    }

    private TrackerTask Read(JsonElement t) => new(
        t.GetProperty("external_id").GetString()!,
        Optional(t, "title") ?? "",
        Optional(t, "description"),
        Optional(t, "due_on") is { } d ? DateOnly.ParseExact(d, "yyyy-MM-dd", CultureInfo.InvariantCulture) : null,
        t.TryGetProperty("done", out var done) && done.ValueKind == JsonValueKind.True,
        t.TryGetProperty("assignees", out var a) && a.ValueKind == JsonValueKind.Array ? a.EnumerateArray().Select(e => e.GetString()!).ToList() : [],
        Optional(t, "version") ?? "",
        Optional(t, "modified_at") is { } m ? DateTimeOffset.Parse(m, CultureInfo.InvariantCulture) : clock.UtcNow);

    private static string? Optional(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
