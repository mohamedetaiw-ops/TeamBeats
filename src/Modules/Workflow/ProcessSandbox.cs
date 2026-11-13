using System.Text.Json;
using System.Text.Json.Nodes;
using TeamBeats.Modules.Identity.Contracts;
using TeamBeats.Modules.People.Contracts;
using TeamBeats.Modules.Workflow.Contracts;
using TeamBeats.Modules.Workflow.Feel;
using TeamBeats.Modules.Workflow.Graph;
using TeamBeats.Primitives;

namespace TeamBeats.Modules.Workflow;

/// <summary>
/// A test run of a draft (spec 024 US1 scenario 4). <paramref name="Outcomes"/> names the outcome per user task (approve
/// when not named); <paramref name="Events"/> lists boundary events and event gateway targets to fire when they are
/// reached; <paramref name="Results"/> gives the mocked result of service tasks, called processes and messages per element.
/// </summary>
public sealed record ProcessTestRequest(
    JsonElement? Variables,
    Guid? RequesterId,
    IReadOnlyDictionary<string, string>? Outcomes,
    IReadOnlyList<string>? Events,
    IReadOnlyDictionary<string, JsonElement>? Results);

public sealed record TestPerson(Guid Id, string Name);

/// <summary>One step the run took. Assignees, permission and line manager say who a user task would go to.</summary>
public sealed record TestStep(string ElementId, string Name, string Event, JsonObject? Detail, IReadOnlyList<TestPerson>? Assignees = null, string? Permission = null,
    bool LineManager = false);

/// <summary>How the run ended (completed, cancelled, an end outcome, incident, waiting or stopped), the steps in order and the variables at the end.</summary>
public sealed record ProcessTestResult(string Outcome, IReadOnlyList<TestStep> Steps, JsonObject Variables, IReadOnlyList<ProblemView> Problems);

/// <summary>
/// Walks a process graph in memory with the engine's own executor and nothing else: no instance, task, job, message,
/// connector call or audit is written. User tasks are completed with the outcome the caller names, timers are
/// fast-forwarded, service tasks, webhooks and called processes are mocked with the caller's results, sent messages go
/// nowhere and decisions use their current published version.
/// </summary>
public sealed class ProcessSandbox(ProcessEngine engine, IReportingLine reportingLine, IUserDirectory users, IClock clock)
{
    public const int MaxActions = 200;

    private sealed class Context(DateTimeOffset now, IReadOnlyDictionary<string, Decisions.DecisionTable> tables) : IExecutionContext
    {
        public DateTimeOffset Now { get; } = now;

        public (JsonNode? Result, IReadOnlyList<int> Rules) Decide(string decisionKey, IReadOnlyDictionary<string, object?> variables) =>
            tables.TryGetValue(decisionKey, out var table) ? table.Evaluate(variables, Now) : throw new DecisionException("decision_missing");
    }

    private enum Work { UserTask, Service, Child }

    public async Task<ProcessTestResult> RunAsync(ProcessGraph graph, ProcessTestRequest request, string locale, CancellationToken ct)
    {
        if (request.RequesterId is { } requester && await users.GetAsync(requester, ct) is null)
        {
            throw AppException.Validation("requesterId", "validation.process.test_requester");
        }
        var variables = request.Variables is { ValueKind: JsonValueKind.Object } v ? (JsonObject)JsonNode.Parse(v.GetRawText())! : [];
        if (request.Variables is { ValueKind: not (JsonValueKind.Object or JsonValueKind.Null or JsonValueKind.Undefined) })
        {
            throw AppException.Validation("variables", "validation.workflow.variables");
        }
        var tables = new Dictionary<string, Decisions.DecisionTable>(StringComparer.Ordinal);
        foreach (var key in graph.Elements.Where(e => e.DecisionKey is not null).Select(e => e.DecisionKey!).Distinct())
        {
            if (await engine.CurrentDecisionAsync(key, ct) is { } table)
            {
                tables[key] = table;
            }
        }
        var executor = new Executor(graph, new InstanceState(), new Context(clock.UtcNow, tables));
        var outcomes = request.Outcomes ?? new Dictionary<string, string>();
        var events = new HashSet<string>(request.Events ?? [], StringComparer.Ordinal);
        var results = request.Results ?? new Dictionary<string, JsonElement>();
        JsonObject? Result(string element) =>
            results.TryGetValue(element, out var r) && r.ValueKind == JsonValueKind.Object ? (JsonObject)JsonNode.Parse(r.GetRawText())! : null;

        var steps = new List<TestStep>();
        var work = new Dictionary<Guid, (Work Kind, string Element)>();
        var waits = new Dictionary<Guid, List<Wait>>();
        var order = new List<Guid>();
        string? outcome = null;
        var incidents = 0;
        var seen = 0;

        var start = graph.InScope(null).FirstOrDefault(e => e.Type == ElementTypes.StartEvent && e.EventKind == EventKinds.None)
            ?? graph.InScope(null).First(e => e.Type == ElementTypes.StartEvent);
        executor.Start(variables, start.EventKind == EventKinds.None ? null : start.Id, start.EventKind == EventKinds.None ? null : Result(start.Id));
        for (var actions = 0; ; actions++)
        {
            for (; seen < executor.Effects.Count; seen++)
            {
                switch (executor.Effects[seen])
                {
                    case Graph.Record r when r.Event is not ("entered" or "task_created"):
                        steps.Add(new TestStep(r.ElementId, Name(graph, r.ElementId, locale), r.Event, r.Detail));
                        break;
                    case CreateUserTask t:
                        var (step, nobody) = await UserTaskStepAsync(graph.Element(t.ElementId), t.Variables, request.RequesterId, locale, ct);
                        steps.Add(step);
                        if (nobody)
                        {
                            // The engine raises an incident here; the run stops this path the same way.
                            incidents++;
                            steps.Add(new TestStep(t.ElementId, step.Name, "incident", new JsonObject { ["class"] = "no_assignee" }));
                        }
                        else
                        {
                            Track(t.Token, Work.UserTask, t.ElementId);
                        }
                        break;
                    case Wait w:
                        if (!waits.TryGetValue(w.Token, out var list))
                        {
                            waits[w.Token] = list = [];
                            if (!order.Contains(w.Token))
                            {
                                order.Add(w.Token);
                            }
                        }
                        list.Add(w);
                        break;
                    case CancelWaits c:
                        waits.Remove(c.Token);
                        break;
                    case CallService s:
                        var call = graph.Element(s.ElementId).Call;
                        steps.Add(new TestStep(s.ElementId, Name(graph, s.ElementId, locale), "mocked",
                            new JsonObject { ["kind"] = call?.WebhookId is null ? "connector" : "webhook", ["inputs"] = s.Inputs.DeepClone() }));
                        Track(s.Token, Work.Service, s.ElementId);
                        break;
                    case StartChild child:
                        steps.Add(new TestStep(child.ElementId, Name(graph, child.ElementId, locale), "mocked", new JsonObject { ["calledKey"] = child.CalledKey }));
                        Track(child.Token, Work.Child, child.ElementId);
                        break;
                    case SendMessage m:
                        steps.Add(new TestStep("", m.Name, "message_not_sent", new JsonObject { ["message"] = m.Name, ["correlationKey"] = m.CorrelationKey }));
                        break;
                    case RaiseIncident i:
                        incidents++;
                        work.Remove(i.Token);
                        waits.Remove(i.Token);
                        steps.Add(new TestStep(i.ElementId, Name(graph, i.ElementId, locale), "incident", new JsonObject { ["class"] = i.ErrorClass, ["code"] = i.Code }));
                        break;
                    case Finished f:
                        outcome = f.Code is null ? f.Outcome : $"{f.Outcome}:{f.Code}";
                        break;
                }
            }
            if (outcome is not null)
            {
                break;
            }
            if (actions >= MaxActions)
            {
                outcome = "stopped";
                break;
            }
            if (!Act())
            {
                outcome = incidents > 0 ? "incident" : "waiting";
                break;
            }
        }
        return new ProcessTestResult(outcome, steps, executor.State.Variables, []);

        void Track(Guid token, Work kind, string element)
        {
            work[token] = (kind, element);
            if (!order.Contains(token))
            {
                order.Add(token);
            }
        }

        // Moves one waiting token on: a named event first, then its work (a task, a service, a called process), then
        // a message or timer that is not on a boundary. Returns false when no token can move.
        bool Act()
        {
            foreach (var token in order.ToList())
            {
                if (!executor.State.Tokens.Any(t => t.Id == token))
                {
                    order.Remove(token);
                    work.Remove(token);
                    waits.Remove(token);
                    continue;
                }
                var tokenWaits = waits.GetValueOrDefault(token) ?? [];
                if (tokenWaits.FirstOrDefault(w => events.Contains(w.EventElementId)) is { } named)
                {
                    events.Remove(named.EventElementId);
                    waits.Remove(token);
                    work.Remove(token);
                    executor.Fire(token, named.EventElementId, named.Kind == EventKinds.Message ? Result(named.EventElementId) : null);
                    return true;
                }
                if (work.Remove(token, out var job))
                {
                    waits.Remove(token);
                    var result = job.Kind == Work.UserTask
                        ? new JsonObject { ["outcome"] = outcomes.GetValueOrDefault(job.Element) ?? TaskOutcomes.Approve, ["completedBy"] = request.RequesterId?.ToString() }
                        : Result(job.Element) ?? [];
                    executor.Complete(token, result);
                    return true;
                }
                var next = tokenWaits.FirstOrDefault(w => graph.Element(w.EventElementId).Type != ElementTypes.BoundaryEvent);
                if (next is not null)
                {
                    waits.Remove(token);
                    if (next.Kind == EventKinds.Timer)
                    {
                        steps.Add(new TestStep(next.EventElementId, Name(graph, next.EventElementId, locale), "fast_forwarded", null));
                    }
                    executor.Fire(token, next.EventElementId, next.Kind == EventKinds.Message ? Result(next.EventElementId) ?? [] : null);
                    return true;
                }
            }
            return false;
        }
    }

    private static string Name(ProcessGraph graph, string id, string locale) => graph.Has(id) ? graph.Element(id).Name(locale) : id;

    /// <summary>Who a user task would go to, without creating it: named users, the requester's managers, the assignee expression and the fallback.</summary>
    private async Task<(TestStep Step, bool Nobody)> UserTaskStepAsync(GraphElement element, JsonObject variables, Guid? requester, string locale, CancellationToken ct)
    {
        var assignment = element.Assignment ?? new Assignment();
        var ids = new List<Guid>(assignment.Users ?? []);
        var lineManager = false;
        if (assignment.LineManager && requester is null)
        {
            // With no sample requester the task would go to whoever the line manager turns out to be.
            lineManager = true;
        }
        else if (assignment.LineManager && requester is { } subject && await reportingLine.ManagerOfAsync(subject, ct) is { } manager)
        {
            ids.Add(manager);
            lineManager = true;
        }
        if (assignment.ManagerLevel is { } level and > 0 && requester is { } person)
        {
            var chain = await reportingLine.ChainAsync(person, level, ct);
            if (chain.Count == level)
            {
                ids.Add(chain[^1]);
            }
        }
        JsonObject? detail = null;
        if (assignment.AssigneeExpression is { } expression)
        {
            try
            {
                var value = FeelEngine.Evaluate(expression, FeelEngine.Variables(variables), clock.UtcNow);
                ids.AddRange(value switch
                {
                    string s when Guid.TryParse(s, out var id) => [id],
                    IEnumerable<object?> list => list.OfType<string>().Select(s => Guid.TryParse(s, out var id) ? id : Guid.Empty).Where(g => g != Guid.Empty),
                    _ => [],
                });
            }
            catch (FeelEvaluationException ex)
            {
                detail = new JsonObject { ["expressionError"] = ex.Code };
            }
        }
        if (ids.Count == 0 && assignment.Permission is null && !lineManager)
        {
            ids.AddRange(assignment.FallbackUsers ?? []);
        }
        var found = await users.GetManyAsync(ids.Distinct().ToList(), ct);
        var people = ids.Distinct().Where(found.ContainsKey).Select(id => new TestPerson(id, found[id].DisplayName)).ToList();
        var nobody = people.Count == 0 && assignment.Permission is null && !lineManager;
        return (new TestStep(element.Id, element.Name(locale), "assigned", detail, people, assignment.Permission, lineManager), nobody);
    }
}
