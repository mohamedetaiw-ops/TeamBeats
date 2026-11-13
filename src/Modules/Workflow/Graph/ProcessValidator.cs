using TeamBeats.Modules.Workflow.Feel;

namespace TeamBeats.Modules.Workflow.Graph;

/// <summary>
/// Validation of a process graph before publishing (spec 024 FR-002): every element reachable, parallel splits matched by a
/// join, user tasks assigned, timers and FEEL that parse, service tasks bound, boundaries attached to activities, gateways
/// well formed. Checks that need the tenant's data (decisions, called processes, connections) run in the service.
/// </summary>
public static class ProcessValidator
{
    public static IReadOnlyList<ModelProblem> Validate(ProcessGraph graph)
    {
        var problems = new List<ModelProblem>();
        void Add(string id, string key) => problems.Add(new ModelProblem(id, "validation.process." + key));
        void Feel(string id, string? expression)
        {
            if (expression is not null && FeelEngine.CheckExpression(expression) is not null)
            {
                Add(id, "expression");
            }
        }

        // Flows join elements of the same scope. A broken flow stops the checks below; a bad condition does not.
        var broken = false;
        foreach (var flow in graph.Flows)
        {
            if (!graph.Has(flow.Source) || !graph.Has(flow.Target) || graph.Element(flow.Source).Scope != graph.Element(flow.Target).Scope)
            {
                Add(flow.Id, "flow");
                broken = true;
                continue;
            }
            if (graph.Element(flow.Target).Type is ElementTypes.StartEvent or ElementTypes.BoundaryEvent || graph.Element(flow.Source).Type == ElementTypes.EndEvent)
            {
                Add(flow.Id, "flow");
                broken = true;
            }
            Feel(flow.Id, flow.Condition);
            if (flow.Condition is not null && graph.Element(flow.Source).Type is not (ElementTypes.ExclusiveGateway or ElementTypes.InclusiveGateway))
            {
                Add(flow.Id, "condition_place");
            }
        }
        if (broken)
        {
            return problems;
        }

        var scopes = new List<string?> { null };
        scopes.AddRange(graph.Elements.Where(e => e.Type == ElementTypes.SubProcess).Select(e => e.Id));
        foreach (var scope in scopes)
        {
            var starts = graph.InScope(scope).Where(e => e.Type == ElementTypes.StartEvent).ToList();
            var owner = scope is null ? null : graph.Element(scope);
            if (owner?.TriggeredByEvent == true)
            {
                if (starts.Count != 1 || starts[0].EventKind is not (EventKinds.Timer or EventKinds.Message or EventKinds.Error or EventKinds.Escalation))
                {
                    Add(scope!, "event_subprocess_start");
                }
            }
            else if (scope is null ? starts.Count == 0 || starts.Count(s => s.EventKind == EventKinds.None) > 1
                         : starts.Count != 1 || starts[0].EventKind != EventKinds.None)
            {
                Add(scope ?? "process", "start");
            }
            if (!graph.InScope(scope).Any(e => e.Type == ElementTypes.EndEvent))
            {
                Add(scope ?? "process", "end");
            }

            // Reachability from the starts of the scope; boundaries are reached through their activity.
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var queue = new Queue<string>(starts.Select(s => s.Id));
            while (queue.TryDequeue(out var id))
            {
                if (!seen.Add(id))
                {
                    continue;
                }
                var element = graph.Element(id);
                foreach (var flow in element.Outgoing)
                {
                    queue.Enqueue(graph.Flow(flow).Target);
                }
                foreach (var boundary in graph.BoundariesOf(id))
                {
                    queue.Enqueue(boundary.Id);
                }
            }
            foreach (var element in graph.InScope(scope))
            {
                var isEventSubProcess = element.Type == ElementTypes.SubProcess && element.TriggeredByEvent;
                if (!seen.Contains(element.Id) && !isEventSubProcess)
                {
                    Add(element.Id, "unreachable");
                }
            }
        }

        foreach (var e in graph.Elements)
        {
            var outgoing = e.Outgoing.Count;
            if (e.Type is not ElementTypes.EndEvent && !(e.Type == ElementTypes.SubProcess && e.TriggeredByEvent) && outgoing == 0)
            {
                Add(e.Id, "no_outgoing");
            }
            if ((ElementTypes.Activities.Contains(e.Type) || e.Type is ElementTypes.StartEvent or ElementTypes.IntermediateCatchEvent or ElementTypes.IntermediateThrowEvent) && outgoing > 1)
            {
                // Activities leave by one flow; branching is done with gateways, so token counts stay clear.
                Add(e.Id, "activity_outgoing");
            }
            switch (e.Type)
            {
                case ElementTypes.StartEvent:
                    if (e.EventKind == EventKinds.Timer && BpmnReader.CheckTimer(e.Timer) is { } t)
                    {
                        Add(e.Id, t["validation.process.".Length..]);
                    }
                    if (e.EventKind == EventKinds.Message && string.IsNullOrEmpty(e.MessageName))
                    {
                        Add(e.Id, "message");
                    }
                    if (e.Scope is null && e.EventKind is EventKinds.Error or EventKinds.Escalation)
                    {
                        Add(e.Id, "start");
                    }
                    break;
                case ElementTypes.EndEvent:
                    if (e.EventKind is EventKinds.Error or EventKinds.Escalation && !BpmnReader.IsCode(e.Code))
                    {
                        Add(e.Id, "code");
                    }
                    if (e.Result is not null && (e.EventKind is not (EventKinds.None or EventKinds.Terminate) || !BpmnReader.IsCode(e.Result)))
                    {
                        Add(e.Id, "result");
                    }
                    break;
                case ElementTypes.IntermediateCatchEvent:
                    CheckCatch(e, Add, Feel);
                    break;
                case ElementTypes.IntermediateThrowEvent:
                    if (e.EventKind == EventKinds.Escalation && !BpmnReader.IsCode(e.Code))
                    {
                        Add(e.Id, "code");
                    }
                    if (e.EventKind == EventKinds.Message)
                    {
                        if (string.IsNullOrEmpty(e.MessageName))
                        {
                            Add(e.Id, "message");
                        }
                        Feel(e.Id, e.CorrelationKey);
                    }
                    if (e.EventKind is not (EventKinds.None or EventKinds.Escalation or EventKinds.Message))
                    {
                        Add(e.Id, "unsupported_element");
                    }
                    break;
                case ElementTypes.BoundaryEvent:
                    if (e.AttachedTo is null || !graph.Has(e.AttachedTo) || !ElementTypes.Activities.Contains(graph.Element(e.AttachedTo).Type)
                        || graph.Element(e.AttachedTo).Scope != e.Scope)
                    {
                        Add(e.Id, "boundary_attach");
                        break;
                    }
                    if (e.EventKind is EventKinds.Error && !e.Interrupting)
                    {
                        Add(e.Id, "boundary_error");
                    }
                    if (e.EventKind is EventKinds.Error && graph.Element(e.AttachedTo).Type is not (ElementTypes.ServiceTask or ElementTypes.SubProcess or ElementTypes.CallActivity))
                    {
                        Add(e.Id, "boundary_error");
                    }
                    if (e.EventKind is EventKinds.None or EventKinds.Terminate)
                    {
                        Add(e.Id, "unsupported_element");
                    }
                    if (e.EventKind is EventKinds.Timer or EventKinds.Message)
                    {
                        CheckCatch(e, Add, Feel);
                    }
                    if (outgoing != 1)
                    {
                        Add(e.Id, "activity_outgoing");
                    }
                    break;
                case ElementTypes.UserTask:
                    if (e.Assignment is null || e.Assignment.IsEmpty || e.Assignment.ManagerLevel is < 1 or > 5 || e.Assignment.Users?.Count > 50
                        || e.Assignment.Groups?.Count > 20)
                    {
                        Add(e.Id, "assignment");
                    }
                    Feel(e.Id, e.Assignment?.AssigneeExpression);
                    if (e.DueWorkingDays is < 1 or > 90)
                    {
                        Add(e.Id, "due");
                    }
                    // An escalation follows the due time, so it needs one, and someone to go to.
                    if (e.Escalation is { } escalation && (e.DueWorkingDays is null || escalation.AfterWorkingDays is < 1 or > 90
                        || escalation.Users.Count + escalation.Groups.Count == 0 || escalation.Users.Count > 50 || escalation.Groups.Count > 20))
                    {
                        Add(e.Id, "escalation");
                    }
                    CheckMappings(e.Id, e.Outputs, Add, Feel);
                    break;
                case ElementTypes.ServiceTask:
                    if (e.Call is null || e.Call.Retries is < 0 or > 10
                        || (e.Call.Product is { } product
                            ? !ProductActions.All.Contains(product) || e.Call.ConnectionId is not null || e.Call.WebhookId is not null
                            : (e.Call.ConnectionId is null) == (e.Call.WebhookId is null) || e.Call.ConnectionId == Guid.Empty || e.Call.WebhookId == Guid.Empty
                                || (e.Call.ConnectionId is not null && string.IsNullOrEmpty(e.Call.Action))))
                    {
                        Add(e.Id, "service_binding");
                        break;
                    }
                    foreach (var source in e.Call.Inputs.Values)
                    {
                        Feel(e.Id, source);
                    }
                    CheckMappings(e.Id, e.Call.Outputs, Add, Feel);
                    break;
                case ElementTypes.BusinessRuleTask:
                    if (!BpmnReader.IsValidKey(e.DecisionKey) || !BpmnReader.IsVariable(e.ResultVariable))
                    {
                        Add(e.Id, "decision");
                    }
                    break;
                case ElementTypes.SendTask:
                case ElementTypes.ReceiveTask:
                    if (string.IsNullOrEmpty(e.MessageName))
                    {
                        Add(e.Id, "message");
                    }
                    if (e.Type == ElementTypes.ReceiveTask && e.CorrelationKey is null)
                    {
                        Add(e.Id, "correlation");
                    }
                    Feel(e.Id, e.CorrelationKey);
                    break;
                case ElementTypes.CallActivity:
                    if (!BpmnReader.IsValidKey(e.CalledKey))
                    {
                        Add(e.Id, "call");
                    }
                    else if (e.CalledKey == graph.Key)
                    {
                        Add(e.Id, "call_self");
                    }
                    CheckMappings(e.Id, e.Outputs, Add, Feel);
                    break;
                case ElementTypes.ExclusiveGateway:
                case ElementTypes.InclusiveGateway:
                    if (outgoing > 1)
                    {
                        foreach (var flowId in e.Outgoing)
                        {
                            if (flowId != e.DefaultFlow && graph.Flow(flowId).Condition is null)
                            {
                                Add(flowId, "condition");
                            }
                        }
                    }
                    if (e.DefaultFlow is not null && !e.Outgoing.Contains(e.DefaultFlow))
                    {
                        Add(e.Id, "default_flow");
                    }
                    break;
                case ElementTypes.EventBasedGateway:
                    if (outgoing < 2 || e.Outgoing.Select(f => graph.Element(graph.Flow(f).Target)).Any(t =>
                            !(t.Type == ElementTypes.ReceiveTask || t.Type == ElementTypes.IntermediateCatchEvent && t.EventKind is EventKinds.Timer or EventKinds.Message)
                            || t.Incoming.Count != 1))
                    {
                        Add(e.Id, "event_gateway");
                    }
                    break;
            }
            if (e.Loop is { } loop)
            {
                if (!ElementTypes.Activities.Contains(e.Type) || !BpmnReader.IsVariable(loop.ElementVariable) || loop.Collection.Length == 0)
                {
                    Add(e.Id, "loop");
                }
                Feel(e.Id, loop.Collection.Length == 0 ? null : loop.Collection);
                Feel(e.Id, loop.CompletionCondition);
            }
        }

        // A parallel split needs a parallel join that every branch reaches.
        foreach (var split in graph.Elements.Where(e => e.Type == ElementTypes.ParallelGateway && e.Outgoing.Count > 1))
        {
            HashSet<string>? common = null;
            foreach (var flow in split.Outgoing)
            {
                var joins = JoinsReachable(graph, graph.Flow(flow).Target, split.Id);
                common = common is null ? joins : common.Intersect(joins, StringComparer.Ordinal).ToHashSet(StringComparer.Ordinal);
            }
            if (common is not { Count: > 0 })
            {
                Add(split.Id, "parallel_join");
            }
        }
        return problems;
    }

    private static HashSet<string> JoinsReachable(ProcessGraph graph, string from, string split)
    {
        var joins = new HashSet<string>(StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var queue = new Queue<string>([from]);
        while (queue.TryDequeue(out var id))
        {
            if (id == split || !seen.Add(id))
            {
                continue;
            }
            var element = graph.Element(id);
            if (element.Type == ElementTypes.ParallelGateway && element.Incoming.Count > 1)
            {
                joins.Add(id);
            }
            foreach (var flow in element.Outgoing)
            {
                queue.Enqueue(graph.Flow(flow).Target);
            }
        }
        return joins;
    }

    private static void CheckCatch(GraphElement e, Action<string, string> add, Action<string, string?> feel)
    {
        switch (e.EventKind)
        {
            case EventKinds.Timer:
                if (BpmnReader.CheckTimer(e.Timer) is { } problem)
                {
                    add(e.Id, problem["validation.process.".Length..]);
                }
                break;
            case EventKinds.Message:
                if (string.IsNullOrEmpty(e.MessageName))
                {
                    add(e.Id, "message");
                }
                if (e.CorrelationKey is null)
                {
                    add(e.Id, "correlation");
                }
                feel(e.Id, e.CorrelationKey);
                break;
            case EventKinds.Error when e.Type == ElementTypes.BoundaryEvent:
            case EventKinds.Escalation when e.Type == ElementTypes.BoundaryEvent:
                break;
            default:
                add(e.Id, "unsupported_element");
                break;
        }
    }

    private static void CheckMappings(string id, IReadOnlyDictionary<string, string>? mappings, Action<string, string> add, Action<string, string?> feel)
    {
        foreach (var (variable, source) in mappings ?? new Dictionary<string, string>())
        {
            if (!BpmnReader.IsVariable(variable))
            {
                add(id, "mapping");
            }
            feel(id, source);
        }
    }
}
