using System.Text.Json;
using TeamBeats.Entitlements.Contracts;
using TeamBeats.Modules.Integrations.Contracts;
using TeamBeats.Modules.Integrations.CustomRest;
using TeamBeats.Modules.Integrations.Runtime;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.Integrations.Ticketing;

/// <summary>
/// Camunda 8 (spec 024 US8, FR-016): the external engine adapter over the Orchestration Cluster REST API (v2).
/// <c>search_user_tasks</c> reads open, assigned user tasks a page at a time, and <c>complete_user_task</c> completes one
/// with the outcome and comment as variables. Process state stays in Camunda; the output holds only the task reference,
/// name, assignee and dates. Behind <c>workflow.bpmn_advanced</c>.
/// </summary>
public sealed class Camunda8Pack(ITenantContext context, RestSignIn signIn, IHttpClientFactory httpClients) : TicketingPack(context, signIn, httpClients)
{
    public const string KindName = "camunda8";
    public const string SearchUserTasks = "search_user_tasks";
    public const string CompleteUserTask = "complete_user_task";
    public const string NotOpen = "not_open";
    public const int PageSize = 100;

    public override string Kind => KindName;

    public override string CapabilityKey => Capabilities.WorkflowBpmnAdvanced;

    protected override IReadOnlyCollection<string> AuthTypes => ["oauth_client_credentials", "bearer", "basic"];

    public override IReadOnlyList<ConnectorActionDefinition> Actions { get; } =
    [
        new(SearchUserTasks, ConnectorSideEffects.Read, true, [InvalidRequest], [new("after", "string", false)]),
        new(CompleteUserTask, ConnectorSideEffects.Write, true, [NotFound, NotOpen],
        [
            new("key", "string", true),
            new("outcome", "string", false),
            new("comment", "string", false),
        ]),
    ];

    protected override string ProbePath(TicketingSettings settings) => "/v2/topology";

    protected override async Task<PackCallResult> CallAsync(PackConnection connection, TicketingSettings settings, string action, JsonElement input, string idempotencyKey,
        CancellationToken ct)
    {
        if (action == SearchUserTasks)
        {
            var page = new Dictionary<string, object> { ["limit"] = PageSize };
            if (Text(input, "after") is { } after)
            {
                if (after.Length > 1000)
                {
                    return PackCallResult.Fatal(ConnectorErrorClasses.InvalidInput);
                }
                page["after"] = after;
            }
            var request = new { filter = new { state = "CREATED" }, sort = new[] { new { field = "creationDate", order = "ASC" } }, page };
            var (failure, body) = await SendAsync(connection, settings, HttpMethod.Post, "/v2/user-tasks/search", request,
                new Dictionary<int, string> { [400] = InvalidRequest }, false, ct);
            return failure ?? Tasks(body);
        }

        var key = Text(input, "key")!;
        if (!NumericId().IsMatch(key))
        {
            return PackCallResult.Fatal(ConnectorErrorClasses.InvalidInput);
        }
        var variables = new Dictionary<string, string?> { ["outcome"] = Cut(Text(input, "outcome"), 100), ["comment"] = Cut(Text(input, "comment"), 2000) }
            .Where(v => v.Value is not null).ToDictionary(v => v.Key, v => v.Value);
        var (completeFailure, _) = await SendAsync(connection, settings, HttpMethod.Post, $"/v2/user-tasks/{key}/completion", new { variables },
            new Dictionary<int, string> { [404] = NotFound, [409] = NotOpen, [400] = InvalidRequest }, true, ct, noContent: true);
        return completeFailure ?? Done(key, new { key });
    }

    /// <summary>The canonical page: tasks with an assignee only, and the cursor of the next page when the page was full.</summary>
    private static PackCallResult Tasks(JsonElement body)
    {
        if (!body.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array)
        {
            return PackCallResult.Technical(ConnectorErrorClasses.InvalidResponse, mayHaveActed: false);
        }
        var tasks = new List<object>();
        foreach (var item in items.EnumerateArray())
        {
            var key = Text((JsonElement?)item, "userTaskKey") ?? Text((JsonElement?)item, "key");
            if (key is null || !NumericId().IsMatch(key) || Cut(Text(item, "assignee"), 320) is not { Length: > 0 } assignee)
            {
                continue;
            }
            tasks.Add(new
            {
                key,
                name = Cut(Text(item, "name") ?? Text(item, "elementId"), 200),
                process = Cut(Text(item, "processName") ?? Text(item, "bpmnProcessId") ?? Text(item, "processDefinitionId"), 200),
                assignee,
                dueAt = Text(item, "dueDate"),
                createdAt = Text(item, "creationDate"),
            });
        }
        var next = items.GetArrayLength() >= PageSize ? Cut(Text(body, "page", "endCursor"), 1000) : null;
        return Done("user-tasks", new { tasks, next });
    }
}
