using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using TeamBeats.Modules.Integrations.Contracts;
using TeamBeats.Modules.Integrations.CustomRest;
using TeamBeats.Modules.Integrations.Runtime;
using TeamBeats.Primitives;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.Integrations.Ticketing;

/// <summary>
/// What the task tracker packs share (spec 029 R3 US10, through spec 017). Four fixed actions return canonical JSON:
/// <c>list_containers</c> (<c>containers</c> with id and name), <c>list_tasks</c> (<c>tasks</c>), <c>create_task</c> and
/// <c>update_task</c> (<c>task</c>). A task carries external_id, title, description (null when the tracker does not
/// list it), due_on, done, assignees (work emails), version and modified_at (null when the tracker does not say).
/// Only these fields leave the pack.
/// </summary>
public abstract partial class TaskTrackerPack(ITenantContext context, RestSignIn signIn, IHttpClientFactory httpClients) : TicketingPack(context, signIn, httpClients)
{
    public const string ListContainers = "list_containers";
    public const string ListTasks = "list_tasks";
    public const string CreateTask = "create_task";
    public const string UpdateTask = "update_task";
    public const int MaxPages = 10;
    public const int MaxTasks = 1000;
    public const int MaxTitle = 255;
    public const int MaxDescription = 10_000;

    public override IReadOnlyList<ConnectorActionDefinition> Actions { get; } =
    [
        new(ListContainers, ConnectorSideEffects.Read, true, []),
        new(ListTasks, ConnectorSideEffects.Read, true, [NotFound], [new("container", "string", true)]),
        new(CreateTask, ConnectorSideEffects.Write, false, [InvalidRequest, NotFound], [new("container", "string", true), new("title", "string", true)]),
        new(UpdateTask, ConnectorSideEffects.Write, true, [InvalidRequest, NotFound],
            [new("container", "string", true), new("external_id", "string", true), new("title", "string", true)]),
    ];

    protected sealed record Input(string Title, string? Description, DateOnly? DueOn, bool Done, IReadOnlyList<string> Assignees);

    protected static Input Read(JsonElement input)
    {
        var due = Text(input, "due_on") is { } d && DateOnly.TryParseExact(d, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day) ? day : (DateOnly?)null;
        var done = input.TryGetProperty("done", out var x) && x.ValueKind == JsonValueKind.True;
        var assignees = input.TryGetProperty("assignees", out var a) && a.ValueKind == JsonValueKind.Array
            ? a.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!).Where(e => Email().IsMatch(e)).Distinct(StringComparer.OrdinalIgnoreCase).Take(20).ToList()
            : [];
        return new Input(Cut(Text(input, "title"), MaxTitle) ?? "", Cut(Text(input, "description"), MaxDescription), due, done, assignees);
    }

    protected static object TaskJson(string id, string title, string? description, DateOnly? due, bool done, IEnumerable<string> assignees, string version, DateTimeOffset? modified) => new
    {
        external_id = id,
        title = Cut(title, MaxTitle),
        description = Cut(description, MaxDescription),
        due_on = due?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        done,
        assignees = assignees.Distinct(StringComparer.OrdinalIgnoreCase).Take(20).ToArray(),
        version = Cut(version, 200),
        modified_at = modified?.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
    };

    protected static PackCallResult Tasks(List<object> tasks) =>
        PackCallResult.Ok(JsonSerializer.SerializeToElement(new { tasks }, Persistence.JsonDefaults.Options), recordCount: tasks.Count);

    protected static PackCallResult One(string id, object task) => Done(id, new { task });

    [GeneratedRegex(@"^[^@\s,;]{1,64}@[^@\s,;]{1,190}$")]
    protected static partial Regex Email();
}

/// <summary>
/// Microsoft Planner over Microsoft Graph (spec 029 R3 US10). The connection signs in by client credentials with the
/// tenant's app registration (Tasks.ReadWrite.All, User.Read.All) and names the Microsoft 365 group whose plans it
/// lists. A plan is a container. Planner keeps descriptions in task details, which are not synced; it has no modified
/// time, so the version is the task's etag and a change is dated when it is seen. Updates send the etag, so a change made
/// in Planner meanwhile is not overwritten blindly.
/// </summary>
public sealed partial class PlannerTaskPack(ITenantContext context, RestSignIn signIn, IHttpClientFactory httpClients) : TaskTrackerPack(context, signIn, httpClients)
{
    public const string KindName = TaskTrackerKinds.Planner;

    public override string Kind => KindName;

    protected override IReadOnlyCollection<string> AuthTypes => ["oauth_client_credentials", "bearer"];

    protected override string ProbePath(TicketingSettings settings) => $"/groups/{settings.GroupId}/planner/plans";

    [GeneratedRegex("^[A-Za-z0-9_-]{1,64}$")]
    private static partial Regex PlannerId();

    protected override TicketingSettings Extra(TicketingSettings s, ValidationErrors errors)
    {
        if (s.GroupId is null || !Guid.TryParse(s.GroupId, out _))
        {
            errors.Add("groupId", "validation.tasks.planner_group");
        }
        return s with { ServiceDeskId = null, RequestTypeId = null };
    }

    private static readonly IReadOnlyDictionary<int, string> Missing = new Dictionary<int, string> { [404] = NotFound };
    private static readonly IReadOnlyDictionary<int, string> Writes = new Dictionary<int, string> { [400] = InvalidRequest, [404] = NotFound, [412] = InvalidRequest };

    protected override async Task<PackCallResult> CallAsync(PackConnection connection, TicketingSettings settings, string action, JsonElement input, string idempotencyKey,
        CancellationToken ct)
    {
        var emails = new Dictionary<string, string?>(StringComparer.Ordinal);
        switch (action)
        {
            case ListContainers:
            {
                var (failure, body) = await SendAsync(connection, settings, HttpMethod.Get, $"/groups/{settings.GroupId}/planner/plans?$select=id,title", null, Missing, false, ct);
                if (failure is not null)
                {
                    return failure;
                }
                var plans = Values(body).Select(p => new { id = Text(p, "id"), name = Cut(Text(p, "title")) }).Where(p => p.id is not null).ToList();
                return PackCallResult.Ok(JsonSerializer.SerializeToElement(new { containers = plans }, Persistence.JsonDefaults.Options), recordCount: plans.Count);
            }
            case ListTasks:
            {
                var plan = Text(input, "container")!;
                if (!PlannerId().IsMatch(plan))
                {
                    return PackCallResult.Fatal(ConnectorErrorClasses.InvalidInput);
                }
                var tasks = new List<object>();
                string? path = $"/planner/plans/{plan}/tasks";
                for (var page = 0; path is not null && page < MaxPages && tasks.Count < MaxTasks; page++)
                {
                    var (failure, body) = await SendAsync(connection, settings, HttpMethod.Get, path, null, Missing, false, ct);
                    if (failure is not null)
                    {
                        return failure;
                    }
                    foreach (var item in Values(body))
                    {
                        if (await TaskOfAsync(connection, settings, item, emails, ct) is { } task)
                        {
                            tasks.Add(task);
                        }
                    }
                    path = Next(settings, Text(body, "@odata.nextLink"));
                }
                return Tasks(tasks);
            }
            case CreateTask:
            {
                var plan = Text(input, "container")!;
                if (!PlannerId().IsMatch(plan))
                {
                    return PackCallResult.Fatal(ConnectorErrorClasses.InvalidInput);
                }
                var given = Read(input);
                var assignments = new Dictionary<string, object?>();
                foreach (var email in given.Assignees)
                {
                    if (await UserIdAsync(connection, settings, email, ct) is { } id)
                    {
                        assignments[id] = Assignment;
                    }
                }
                var (failure, created) = await SendAsync(connection, settings, HttpMethod.Post, "/planner/tasks",
                    new Dictionary<string, object?>
                    {
                        ["planId"] = plan, ["title"] = given.Title, ["dueDateTime"] = Due(given.DueOn), ["percentComplete"] = given.Done ? 100 : 0, ["assignments"] = assignments,
                    }, Writes, true, ct);
                if (failure is not null)
                {
                    return failure;
                }
                return await TaskOfAsync(connection, settings, created, emails, ct) is { } task
                    ? One(Text(created, "id")!, task)
                    : PackCallResult.Technical(ConnectorErrorClasses.InvalidResponse, mayHaveActed: true);
            }
            case UpdateTask:
            {
                var id = Text(input, "external_id")!;
                if (!PlannerId().IsMatch(id))
                {
                    return PackCallResult.Fatal(ConnectorErrorClasses.InvalidInput);
                }
                var (readFailure, current) = await SendAsync(connection, settings, HttpMethod.Get, $"/planner/tasks/{id}", null, Missing, false, ct);
                if (readFailure is not null)
                {
                    return readFailure;
                }
                var given = Read(input);
                var wanted = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var email in given.Assignees)
                {
                    if (await UserIdAsync(connection, settings, email, ct) is { } user)
                    {
                        wanted[user] = email;
                    }
                }
                var assignments = new Dictionary<string, object?>();
                foreach (var held in AssignedIds(current).Where(a => !wanted.ContainsKey(a)))
                {
                    assignments[held] = null;
                }
                foreach (var user in wanted.Keys.Where(u => !AssignedIds(current).Contains(u)))
                {
                    assignments[user] = Assignment;
                }
                var headers = new Dictionary<string, string> { ["If-Match"] = Text(current, "@odata.etag") ?? "*", ["Prefer"] = "return=representation" };
                var (failure, updated) = await SendAsync(connection, settings, HttpMethod.Patch, $"/planner/tasks/{id}",
                    new Dictionary<string, object?> { ["title"] = given.Title, ["dueDateTime"] = Due(given.DueOn), ["percentComplete"] = given.Done ? 100 : 0, ["assignments"] = assignments },
                    Writes, true, ct, headers: headers);
                if (failure is not null)
                {
                    return failure;
                }
                return await TaskOfAsync(connection, settings, updated, emails, ct) is { } task
                    ? One(id, task)
                    : PackCallResult.Technical(ConnectorErrorClasses.InvalidResponse, mayHaveActed: true);
            }
            default:
                return PackCallResult.Fatal(ConnectorErrorClasses.UnknownAction);
        }
    }

    private static readonly object Assignment = new Dictionary<string, string> { ["@odata.type"] = "#microsoft.graph.plannerAssignment", ["orderHint"] = " !" };

    private static string? Due(DateOnly? day) => day?.ToString("yyyy-MM-dd'T'00:00:00'Z'", CultureInfo.InvariantCulture);

    private static IEnumerable<JsonElement> Values(JsonElement body) =>
        body.ValueKind == JsonValueKind.Object && body.TryGetProperty("value", out var v) && v.ValueKind == JsonValueKind.Array ? v.EnumerateArray() : [];

    private static HashSet<string> AssignedIds(JsonElement task) =>
        task.TryGetProperty("assignments", out var a) && a.ValueKind == JsonValueKind.Object ? a.EnumerateObject().Select(p => p.Name).ToHashSet(StringComparer.Ordinal) : [];

    /// <summary>A next page link on the same Graph address, as a path after it; anything else ends the paging.</summary>
    private static string? Next(TicketingSettings settings, string? link)
    {
        var root = settings.BaseUrl.TrimEnd('/');
        return link is not null && link.StartsWith(root + "/", StringComparison.OrdinalIgnoreCase) ? link[root.Length..] : null;
    }

    private async Task<object?> TaskOfAsync(PackConnection connection, TicketingSettings settings, JsonElement item, Dictionary<string, string?> emails, CancellationToken ct)
    {
        if (Text(item, "id") is not { } id || Text(item, "title") is not { } title)
        {
            return null;
        }
        var due = Text(item, "dueDateTime") is { Length: >= 10 } d && DateOnly.TryParseExact(d[..10], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day)
            ? day
            : (DateOnly?)null;
        var done = item.TryGetProperty("percentComplete", out var p) && p.ValueKind == JsonValueKind.Number && p.GetInt32() >= 100;
        var people = new List<string>();
        foreach (var user in AssignedIds(item))
        {
            if (!emails.TryGetValue(user, out var email))
            {
                emails[user] = email = await EmailAsync(connection, settings, user, ct);
            }
            if (email is not null)
            {
                people.Add(email);
            }
        }
        return TaskJson(id, title, null, due, done, people, Text(item, "@odata.etag") ?? "", null);
    }

    private async Task<string?> EmailAsync(PackConnection connection, TicketingSettings settings, string userId, CancellationToken ct)
    {
        if (!PlannerId().IsMatch(userId))
        {
            return null;
        }
        var (failure, user) = await SendAsync(connection, settings, HttpMethod.Get, $"/users/{userId}?$select=mail,userPrincipalName", null, Missing, false, ct);
        return failure is null ? Text(user, "mail") ?? Text(user, "userPrincipalName") : null;
    }

    private async Task<string?> UserIdAsync(PackConnection connection, TicketingSettings settings, string email, CancellationToken ct)
    {
        var (failure, user) = await SendAsync(connection, settings, HttpMethod.Get, $"/users/{Uri.EscapeDataString(email)}?$select=id", null, Missing, false, ct);
        return failure is null && Text(user, "id") is { } id && PlannerId().IsMatch(id) ? id : null;
    }
}

/// <summary>
/// Jira Software over Jira Cloud REST v3 (spec 029 R3 US10). A project is a container, named by its key. Descriptions
/// go to and from Atlassian document format as plain paragraphs. Done is the "done" status category, reached through a
/// transition. Jira has one assignee, found by work email. Creating an issue takes no idempotency key, so a timeout
/// after sending stops as "outcome unknown" instead of risking a second issue.
/// </summary>
public sealed partial class JiraTaskPack(ITenantContext context, RestSignIn signIn, IHttpClientFactory httpClients) : TaskTrackerPack(context, signIn, httpClients)
{
    public const string KindName = TaskTrackerKinds.Jira;
    private static readonly string[] Fields = ["summary", "description", "duedate", "status", "assignee", "updated"];

    public override string Kind => KindName;

    protected override IReadOnlyCollection<string> AuthTypes => ["basic", "bearer"];

    protected override string ProbePath(TicketingSettings settings) => "/rest/api/3/myself";

    [GeneratedRegex("^[A-Z][A-Z0-9_]{1,19}$")]
    private static partial Regex ProjectKey();

    [GeneratedRegex("^[A-Z][A-Z0-9_]{1,19}-[0-9]{1,10}$")]
    private static partial Regex IssueKey();

    [GeneratedRegex(@"([+-]\d{2})(\d{2})$")]
    private static partial Regex Offset();

    private static readonly IReadOnlyDictionary<int, string> Missing = new Dictionary<int, string> { [404] = NotFound };
    private static readonly IReadOnlyDictionary<int, string> Writes = new Dictionary<int, string> { [400] = InvalidRequest, [404] = NotFound };

    protected override async Task<PackCallResult> CallAsync(PackConnection connection, TicketingSettings settings, string action, JsonElement input, string idempotencyKey,
        CancellationToken ct)
    {
        switch (action)
        {
            case ListContainers:
            {
                var (failure, body) = await SendAsync(connection, settings, HttpMethod.Get, "/rest/api/3/project/search?maxResults=100&orderBy=name", null, null, false, ct);
                if (failure is not null)
                {
                    return failure;
                }
                var projects = (body.TryGetProperty("values", out var v) && v.ValueKind == JsonValueKind.Array ? v.EnumerateArray() : [])
                    .Select(p => new { id = Text(p, "key"), name = Cut(Text(p, "name")) }).Where(p => p.id is not null && ProjectKey().IsMatch(p.id)).ToList();
                return PackCallResult.Ok(JsonSerializer.SerializeToElement(new { containers = projects }, Persistence.JsonDefaults.Options), recordCount: projects.Count);
            }
            case ListTasks:
            {
                var project = Text(input, "container")!;
                if (!ProjectKey().IsMatch(project))
                {
                    return PackCallResult.Fatal(ConnectorErrorClasses.InvalidInput);
                }
                var tasks = new List<object>();
                string? token = null;
                for (var page = 0; page < MaxPages && tasks.Count < MaxTasks; page++)
                {
                    var query = new Dictionary<string, object?> { ["jql"] = $"project = \"{project}\" ORDER BY updated DESC", ["fields"] = Fields, ["maxResults"] = 100 };
                    if (token is not null)
                    {
                        query["nextPageToken"] = token;
                    }
                    var (failure, body) = await SendAsync(connection, settings, HttpMethod.Post, "/rest/api/3/search/jql", query, Missing, false, ct);
                    if (failure is not null)
                    {
                        return failure;
                    }
                    if (body.TryGetProperty("issues", out var issues) && issues.ValueKind == JsonValueKind.Array)
                    {
                        tasks.AddRange(issues.EnumerateArray().Select(TaskOf).OfType<object>());
                    }
                    token = Text(body, "nextPageToken");
                    if (token is null || (body.TryGetProperty("isLast", out var last) && last.ValueKind == JsonValueKind.True))
                    {
                        break;
                    }
                }
                return Tasks(tasks);
            }
            case CreateTask:
            {
                var project = Text(input, "container")!;
                if (!ProjectKey().IsMatch(project))
                {
                    return PackCallResult.Fatal(ConnectorErrorClasses.InvalidInput);
                }
                var given = Read(input);
                var fields = await FieldsAsync(connection, settings, given, ct);
                fields["project"] = new { key = project };
                fields["issuetype"] = new { name = "Task" };
                var (failure, created) = await SendAsync(connection, settings, HttpMethod.Post, "/rest/api/3/issue", new { fields }, Writes, true, ct);
                if (failure is not null)
                {
                    return failure;
                }
                if (Text(created, "key") is not { } key || !IssueKey().IsMatch(key))
                {
                    return PackCallResult.Technical(ConnectorErrorClasses.InvalidResponse, mayHaveActed: true);
                }
                return await FinishAsync(connection, settings, key, given.Done, ct);
            }
            case UpdateTask:
            {
                var key = Text(input, "external_id")!;
                if (!IssueKey().IsMatch(key))
                {
                    return PackCallResult.Fatal(ConnectorErrorClasses.InvalidInput);
                }
                var given = Read(input);
                var fields = await FieldsAsync(connection, settings, given, ct);
                if (!fields.ContainsKey("assignee"))
                {
                    fields["assignee"] = null;
                }
                var (failure, _) = await SendAsync(connection, settings, HttpMethod.Put, $"/rest/api/3/issue/{key}", new { fields }, Writes, true, ct, noContent: true);
                return failure ?? await FinishAsync(connection, settings, key, given.Done, ct);
            }
            default:
                return PackCallResult.Fatal(ConnectorErrorClasses.UnknownAction);
        }
    }

    private async Task<Dictionary<string, object?>> FieldsAsync(PackConnection connection, TicketingSettings settings, Input given, CancellationToken ct)
    {
        var fields = new Dictionary<string, object?>
        {
            ["summary"] = given.Title,
            ["description"] = Adf(given.Description),
            ["duedate"] = given.DueOn?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        };
        foreach (var email in given.Assignees)
        {
            var (failure, users) = await SendAsync(connection, settings, HttpMethod.Get, $"/rest/api/3/user/search?query={Uri.EscapeDataString(email)}&maxResults=1", null, null, false, ct);
            if (failure is null && users.ValueKind == JsonValueKind.Array && users.GetArrayLength() > 0 && Text(users[0], "accountId") is { } account)
            {
                fields["assignee"] = new { accountId = account };
                break;
            }
        }
        return fields;
    }

    /// <summary>Moves the issue to a done or a to-do status when it is not there yet, then reads it back.</summary>
    private async Task<PackCallResult> FinishAsync(PackConnection connection, TicketingSettings settings, string key, bool done, CancellationToken ct)
    {
        var path = $"/rest/api/3/issue/{key}?fields={string.Join(',', Fields)}";
        var (failure, issue) = await SendAsync(connection, settings, HttpMethod.Get, path, null, Missing, false, ct);
        if (failure is not null)
        {
            return failure;
        }
        if (IsDone(issue) != done)
        {
            var (listFailure, list) = await SendAsync(connection, settings, HttpMethod.Get, $"/rest/api/3/issue/{key}/transitions", null, Missing, false, ct);
            if (listFailure is not null)
            {
                return listFailure;
            }
            var wanted = done ? "done" : "new";
            var transition = (list.TryGetProperty("transitions", out var t) && t.ValueKind == JsonValueKind.Array ? t.EnumerateArray() : [])
                .FirstOrDefault(x => Text(x, "to", "statusCategory", "key") == wanted);
            if (transition.ValueKind == JsonValueKind.Object && Text(transition, "id") is { } id)
            {
                var (moveFailure, _) = await SendAsync(connection, settings, HttpMethod.Post, $"/rest/api/3/issue/{key}/transitions", new { transition = new { id } }, Writes, true, ct,
                    noContent: true);
                if (moveFailure is not null)
                {
                    return moveFailure;
                }
                (failure, issue) = await SendAsync(connection, settings, HttpMethod.Get, path, null, Missing, false, ct);
                if (failure is not null)
                {
                    return failure;
                }
            }
        }
        return TaskOf(issue) is { } task ? One(key, task) : PackCallResult.Technical(ConnectorErrorClasses.InvalidResponse, mayHaveActed: true);
    }

    private static bool IsDone(JsonElement issue) => Text(issue, "fields", "status", "statusCategory", "key") == "done";

    private object? TaskOf(JsonElement issue)
    {
        if (Text(issue, "key") is not { } key || !IssueKey().IsMatch(key) || !issue.TryGetProperty("fields", out var f) || Text(f, "summary") is not { } title)
        {
            return null;
        }
        var due = Text(f, "duedate") is { } d && DateOnly.TryParseExact(d, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day) ? day : (DateOnly?)null;
        var updated = Text(f, "updated");
        DateTimeOffset? modified = updated is not null && DateTimeOffset.TryParse(Offset().Replace(updated, "$1:$2"), CultureInfo.InvariantCulture, DateTimeStyles.None, out var at)
            ? at
            : null;
        var description = f.TryGetProperty("description", out var doc) ? PlainText(doc) : null;
        var assignee = Text(f, "assignee", "emailAddress");
        return TaskJson(key, title, description, due, IsDone(issue), assignee is null ? [] : [assignee], updated ?? "", modified);
    }

    /// <summary>Plain paragraphs as an Atlassian document; null clears the description.</summary>
    internal static object? Adf(string? text) => string.IsNullOrWhiteSpace(text)
        ? null
        : new
        {
            type = "doc",
            version = 1,
            content = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split("\n\n").Select(p => new
            {
                type = "paragraph",
                content = new[] { new { type = "text", text = p } },
            }).ToArray(),
        };

    /// <summary>The text of an Atlassian document: its text nodes, with a blank line between blocks.</summary>
    internal static string? PlainText(JsonElement doc)
    {
        if (doc.ValueKind == JsonValueKind.String)
        {
            return doc.GetString();
        }
        if (doc.ValueKind != JsonValueKind.Object || !doc.TryGetProperty("content", out var blocks) || blocks.ValueKind != JsonValueKind.Array)
        {
            return null;
        }
        var parts = blocks.EnumerateArray().Select(Inline).Where(p => p.Length > 0).ToList();
        return parts.Count == 0 ? null : string.Join("\n\n", parts);

        static string Inline(JsonElement node)
        {
            if (node.ValueKind != JsonValueKind.Object)
            {
                return "";
            }
            if (node.TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.String)
            {
                return t.GetString()!;
            }
            return node.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.Array ? string.Concat(c.EnumerateArray().Select(Inline)) : "";
        }
    }
}
