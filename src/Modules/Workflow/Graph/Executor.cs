using System.Text.Json.Nodes;
using TeamBeats.Modules.Workflow.Feel;

namespace TeamBeats.Modules.Workflow.Graph;

/// <summary>A token: where one path of an instance is, in which scope. A token waits at an element until a signal moves it.</summary>
public sealed class TokenState
{
    public Guid Id { get; set; }
    public string ElementId { get; set; } = "";
    public Guid Scope { get; set; }

    /// <summary>The flow the token arrived by, for joins.</summary>
    public string? Via { get; set; }

    /// <summary><c>waiting</c>, <c>joining</c>, <c>listening</c> (event sub-process start) or <c>incident</c>.</summary>
    public string Status { get; set; } = "waiting";

    /// <summary>For a token inside a multi-instance body: it runs the activity itself, not the loop around it.</summary>
    public bool Body { get; set; }
}

/// <summary>A scope opened by a sub-process, a multi-instance body or an event sub-process; the root scope is <see cref="Guid.Empty"/>.</summary>
public sealed class ScopeState
{
    public Guid Id { get; set; }
    public string ElementId { get; set; } = "";
    public Guid Parent { get; set; }

    /// <summary>The token waiting at the element that opened the scope.</summary>
    public Guid Owner { get; set; }

    public string Kind { get; set; } = "subprocess";
    public JsonObject Locals { get; set; } = [];

    // Multi-instance loop state, kept on the scope of the loop (Kind "loop"); bodies are child scopes of Kind "body".
    public List<JsonNode?>? Items { get; set; }
    public int Next { get; set; }
    public int Completed { get; set; }
    public bool Sequential { get; set; }
}

/// <summary>The execution state of a graph instance, stored with the instance and changed under its row lock.</summary>
public sealed class InstanceState
{
    public List<TokenState> Tokens { get; set; } = [];
    public List<ScopeState> Scopes { get; set; } = [];
    public Dictionary<string, int> Visits { get; set; } = new(StringComparer.Ordinal);
    public JsonObject Variables { get; set; } = [];

    /// <summary>The result named by the last end event of the process reached (<c>tb:result</c>), for the instance outcome.</summary>
    public string? Result { get; set; }
}

/// <summary>What the executor asks the engine to do in the database after a run, in order.</summary>
public abstract record Effect;

public sealed record CreateUserTask(Guid Token, string ElementId, JsonObject Variables) : Effect;

/// <summary>Wait for a timer or a message for <paramref name="Token"/> at <paramref name="EventElementId"/> (the event, boundary or gateway target).</summary>
public sealed record Wait(Guid Token, string EventElementId, string Kind, TimerSpec? Timer, string? MessageName, string? CorrelationKey) : Effect;

/// <summary>Cancel every open user task, timer, subscription, service call and child instance of the token.</summary>
public sealed record CancelWaits(Guid Token) : Effect;

public sealed record CallService(Guid Token, string ElementId, JsonObject Inputs) : Effect;

public sealed record SendMessage(string Name, string? CorrelationKey, JsonObject Payload) : Effect;

public sealed record StartChild(Guid Token, string ElementId, string CalledKey, JsonObject Variables) : Effect;

public sealed record Record(string ElementId, string Event, JsonObject? Detail = null) : Effect;

public sealed record RaiseIncident(Guid Token, string ElementId, string ErrorClass, string? Code = null) : Effect;

/// <summary>The instance ended: <c>completed</c>, <c>terminated</c>, or <c>error</c> with a code no one caught inside it.</summary>
public sealed record Finished(string Outcome, string? Code = null, string? Result = null) : Effect;

/// <summary>An escalation left the instance uncaught; a parent call activity may catch it.</summary>
public sealed record Escalated(string Code) : Effect;

/// <summary>What the executor needs from outside: the clock and decision tables. Pure otherwise, so it is tested without a database.</summary>
public interface IExecutionContext
{
    DateTimeOffset Now { get; }

    /// <summary>Evaluates the current version of a decision; returns the result and the matched rule numbers, or throws.</summary>
    (JsonNode? Result, IReadOnlyList<int> Rules) Decide(string decisionKey, IReadOnlyDictionary<string, object?> variables);
}

/// <summary>
/// The token executor of the R2 subset (spec 024 FR-004): moves tokens through the graph until each one waits or ends, and
/// returns the effects for the engine to apply. Semantics: activities and events leave by their one flow; exclusive gateways
/// take the first true condition or the default; parallel gateways split to all flows and join when a token came by every
/// incoming flow; inclusive gateways split to every true condition and join when no other token of the scope can still
/// reach them; event-based gateways wait for the first of their targets. A token passing one element more than
/// <see cref="MaxVisits"/> times raises an incident.
/// </summary>
public sealed class Executor(ProcessGraph graph, InstanceState state, IExecutionContext context)
{
    public const int MaxVisits = 1000;
    public const int MaxVariableBytes = 256 * 1024;
    public const int MaxInstanceBytes = 2 * 1024 * 1024;
    private const int MaxSteps = 20_000;

    private readonly List<Effect> effects = [];
    private readonly Queue<TokenState> ready = new();
    private int steps;
    private bool finished;

    public IReadOnlyList<Effect> Effects => effects;

    public InstanceState State => state;

    // ---- Signals ----

    /// <summary>Starts the instance at the none start event, or at <paramref name="startElementId"/> (a timer or message start).</summary>
    public IReadOnlyList<Effect> Start(JsonObject variables, string? startElementId = null, JsonObject? payload = null)
    {
        state.Variables = variables;
        var start = startElementId is null
            ? graph.InScope(null).Single(e => e.Type == ElementTypes.StartEvent && e.EventKind == EventKinds.None)
            : graph.Element(startElementId);
        if (payload is not null)
        {
            Merge(start, payload);
        }
        OpenScope(Guid.Empty);
        Enqueue(NewToken(start.Id, Guid.Empty, null));
        Drain();
        return effects;
    }

    /// <summary>A user task, service task, receive task or call activity of the token finished, with its result.</summary>
    public IReadOnlyList<Effect> Complete(Guid tokenId, JsonObject? result)
    {
        var token = Token(tokenId);
        var element = graph.Element(token.ElementId);
        if (result is not null)
        {
            var before = (JsonObject)state.Variables.DeepClone();
            try
            {
                Apply(element, result, token);
            }
            catch (FeelEvaluationException ex)
            {
                state.Variables = before;
                Incident(token, "expression", ex.Code);
                return effects;
            }
            if (TooLarge())
            {
                state.Variables = before;
                Incident(token, "variables_too_large");
                return effects;
            }
        }
        Record(element.Id, "completed");
        Leave(token);
        Drain();
        return effects;
    }

    /// <summary>A timer or message for <paramref name="eventElementId"/> fired for the token (or for an event sub-process listener).</summary>
    public IReadOnlyList<Effect> Fire(Guid tokenId, string eventElementId, JsonObject? payload)
    {
        var token = state.Tokens.FirstOrDefault(t => t.Id == tokenId);
        if (token is null)
        {
            return effects;
        }
        var target = graph.Element(eventElementId);
        if (payload is not null)
        {
            Merge(target, payload);
        }
        var at = graph.Element(token.ElementId);
        if (token.Status == "listening")
        {
            StartEventSubProcess(token, target);
        }
        else if (target.Id == at.Id)
        {
            Record(at.Id, "fired");
            Leave(token);
        }
        else if (at.Type == ElementTypes.EventBasedGateway)
        {
            effects.Add(new CancelWaits(token.Id));
            Remove(token);
            Record(at.Id, "event:" + target.Id);
            var moved = NewToken(target.Id, token.Scope, null);
            Record(target.Id, "fired");
            if (target.Type == ElementTypes.ReceiveTask)
            {
                Record(target.Id, "completed");
            }
            Leave(moved);
        }
        else if (target.Type == ElementTypes.BoundaryEvent && target.AttachedTo == at.Id)
        {
            Boundary(token, target);
        }
        Drain();
        return effects;
    }

    /// <summary>A service task or child instance of the token ended with a business error.</summary>
    public IReadOnlyList<Effect> ThrowError(Guid tokenId, string code)
    {
        var token = Token(tokenId);
        PropagateError(token, code);
        Drain();
        return effects;
    }

    /// <summary>A child instance escalated: the call activity's boundary or the scope may catch it.</summary>
    public IReadOnlyList<Effect> ThrowEscalation(Guid tokenId, string code)
    {
        var token = Token(tokenId);
        Escalate(token.ElementId, token.Scope, code, token);
        Drain();
        return effects;
    }

    /// <summary>Runs the token's element again (an operator retried an incident).</summary>
    public IReadOnlyList<Effect> Retry(Guid tokenId)
    {
        var token = Token(tokenId);
        token.Status = "waiting";
        state.Visits[token.ElementId] = Math.Max(0, state.Visits.GetValueOrDefault(token.ElementId) - 1);
        Enter(token);
        Drain();
        return effects;
    }

    /// <summary>Moves the token past its element without running it (an operator skipped with a reason).</summary>
    public IReadOnlyList<Effect> Skip(Guid tokenId)
    {
        var token = Token(tokenId);
        token.Status = "waiting";
        Record(token.ElementId, "skipped");
        Leave(token);
        Drain();
        return effects;
    }

    /// <summary>Ends the instance: every wait is cancelled.</summary>
    public IReadOnlyList<Effect> Cancel()
    {
        foreach (var token in state.Tokens.ToList())
        {
            effects.Add(new CancelWaits(token.Id));
        }
        state.Tokens.Clear();
        state.Scopes.Clear();
        finished = true;
        effects.Add(new Finished("cancelled"));
        return effects;
    }

    // ---- Movement ----

    private TokenState Token(Guid id) => state.Tokens.FirstOrDefault(t => t.Id == id) ?? throw new InvalidOperationException("Unknown token.");

    private TokenState NewToken(string elementId, Guid scope, string? via, bool body = false)
    {
        var token = new TokenState { Id = Guid.NewGuid(), ElementId = elementId, Scope = scope, Via = via, Body = body };
        state.Tokens.Add(token);
        return token;
    }

    private void Enqueue(TokenState token) => ready.Enqueue(token);

    private void Remove(TokenState token) => state.Tokens.Remove(token);

    private void Record(string elementId, string evt, JsonObject? detail = null) => effects.Add(new Record(elementId, evt, detail));

    private void Drain()
    {
        while (!finished && ready.TryDequeue(out var token))
        {
            if (state.Tokens.Contains(token))
            {
                Enter(token);
            }
        }
    }

    /// <summary>Spec 024 edge cases: over 256 KB for one variable or 2 MB for the instance.</summary>
    public bool TooLarge() =>
        state.Variables.ToJsonString().Length > MaxInstanceBytes || state.Variables.Any(p => (p.Value?.ToJsonString().Length ?? 0) > MaxVariableBytes);

    /// <summary>The variables a FEEL expression of the token sees: the instance variables under the locals of its scopes.</summary>
    private Dictionary<string, object?> Scope(Guid scope)
    {
        var vars = FeelEngine.Variables(state.Variables);
        var chain = new List<ScopeState>();
        for (var s = state.Scopes.FirstOrDefault(x => x.Id == scope); s is not null && s.Id != Guid.Empty; s = state.Scopes.FirstOrDefault(x => x.Id == s.Parent))
        {
            chain.Add(s);
        }
        foreach (var s in Enumerable.Reverse(chain))
        {
            foreach (var p in s.Locals)
            {
                vars[p.Key] = FeelEngine.FromJson(p.Value);
            }
        }
        return vars;
    }

    private object? Eval(string expression, Guid scope) => FeelEngine.Evaluate(expression, Scope(scope), context.Now);

    private void Enter(TokenState token)
    {
        if (++steps > MaxSteps)
        {
            Incident(token, "loop");
            return;
        }
        var element = graph.Element(token.ElementId);
        var visits = state.Visits.GetValueOrDefault(element.Id) + 1;
        state.Visits[element.Id] = visits;
        if (visits > MaxVisits)
        {
            Incident(token, "loop");
            return;
        }
        try
        {
            if (element.Loop is not null && !token.Body)
            {
                StartLoop(token, element);
                return;
            }
            switch (element.Type)
            {
                case ElementTypes.StartEvent:
                    Record(element.Id, "started");
                    Leave(token);
                    break;
                case ElementTypes.EndEvent:
                    End(token, element);
                    break;
                case ElementTypes.ManualTask:
                    Record(element.Id, "completed");
                    Leave(token);
                    break;
                case ElementTypes.IntermediateThrowEvent:
                    Throw(token, element);
                    break;
                case ElementTypes.IntermediateCatchEvent:
                case ElementTypes.ReceiveTask:
                    RegisterBoundaries(token, element);
                    WaitFor(token, element, element);
                    break;
                case ElementTypes.UserTask:
                    RegisterBoundaries(token, element);
                    effects.Add(new CreateUserTask(token.Id, element.Id, Snapshot(token.Scope)));
                    Record(element.Id, "task_created");
                    break;
                case ElementTypes.ServiceTask:
                {
                    RegisterBoundaries(token, element);
                    var inputs = new JsonObject();
                    foreach (var (name, source) in element.Call!.Inputs)
                    {
                        inputs[name] = FeelEngine.ToJson(Eval(source, token.Scope));
                    }
                    effects.Add(new CallService(token.Id, element.Id, inputs));
                    Record(element.Id, "called");
                    break;
                }
                case ElementTypes.BusinessRuleTask:
                {
                    var (result, rules) = context.Decide(element.DecisionKey!, Scope(token.Scope));
                    state.Variables[element.ResultVariable!] = result?.DeepClone();
                    if (TooLarge())
                    {
                        throw new FeelEvaluationException("variables_too_large");
                    }
                    Record(element.Id, "decided", new JsonObject { ["decision"] = element.DecisionKey, ["rules"] = new JsonArray(rules.Select(r => (JsonNode?)r).ToArray()) });
                    Leave(token);
                    break;
                }
                case ElementTypes.SendTask:
                    Send(token, element);
                    Leave(token);
                    break;
                case ElementTypes.SubProcess:
                {
                    RegisterBoundaries(token, element);
                    var scope = new ScopeState { Id = Guid.NewGuid(), ElementId = element.Id, Parent = token.Scope, Owner = token.Id };
                    state.Scopes.Add(scope);
                    OpenScope(scope.Id);
                    Record(element.Id, "entered");
                    var start = graph.InScope(element.Id).Single(e => e.Type == ElementTypes.StartEvent);
                    Enqueue(NewToken(start.Id, scope.Id, null));
                    break;
                }
                case ElementTypes.CallActivity:
                    RegisterBoundaries(token, element);
                    effects.Add(new StartChild(token.Id, element.Id, element.CalledKey!, Snapshot(token.Scope)));
                    Record(element.Id, "called");
                    break;
                case ElementTypes.ExclusiveGateway:
                    Exclusive(token, element);
                    break;
                case ElementTypes.ParallelGateway:
                    Parallel(token, element);
                    break;
                case ElementTypes.InclusiveGateway:
                    Inclusive(token, element);
                    break;
                case ElementTypes.EventBasedGateway:
                    foreach (var flow in element.Outgoing)
                    {
                        WaitFor(token, graph.Element(graph.Flow(flow).Target), graph.Element(graph.Flow(flow).Target));
                    }
                    Record(element.Id, "waiting");
                    break;
                case ElementTypes.BoundaryEvent:
                    Record(element.Id, "fired");
                    Leave(token);
                    break;
                default:
                    Incident(token, "unsupported");
                    break;
            }
        }
        catch (FeelEvaluationException ex)
        {
            Incident(token, "expression", ex.Code);
        }
        catch (FeelSyntaxException)
        {
            Incident(token, "expression");
        }
        catch (DecisionException ex)
        {
            Incident(token, "decision", ex.Code);
        }
    }

    /// <summary>Instance variables with the scope's locals, for a user task or a child instance to read.</summary>
    private JsonObject Snapshot(Guid scope)
    {
        var copy = (JsonObject)state.Variables.DeepClone();
        foreach (var (key, value) in Scope(scope))
        {
            if (!copy.ContainsKey(key))
            {
                copy[key] = FeelEngine.ToJson(value);
            }
        }
        return copy;
    }

    private void WaitFor(TokenState token, GraphElement waiter, GraphElement evt)
    {
        string? key = evt.CorrelationKey is { } k ? FeelText(Eval(k, token.Scope)) : null;
        if (evt.EventKind == EventKinds.Timer)
        {
            effects.Add(new Wait(token.Id, evt.Id, EventKinds.Timer, Resolve(evt.Timer!, token.Scope), null, null));
        }
        else
        {
            effects.Add(new Wait(token.Id, evt.Id, EventKinds.Message, null, evt.MessageName, key));
        }
        if (waiter.Id == token.ElementId)
        {
            Record(waiter.Id, "waiting");
        }
    }

    /// <summary>Timer text that is a FEEL expression (it has a bracket) is evaluated now; plain ISO 8601 stays as written.</summary>
    private TimerSpec Resolve(TimerSpec timer, Guid scope)
    {
        string? Value(string? text) => text is not null && text.Contains('(') ? FeelText(Eval(text, scope)) : text;
        return timer with { Date = Value(timer.Date), Duration = Value(timer.Duration) };
    }

    private static string? FeelText(object? value) => value switch
    {
        null => null,
        string s => s,
        decimal d => d.ToString(System.Globalization.CultureInfo.InvariantCulture),
        _ => FeelEngine.ToJson(value)?.ToJsonString().Trim('"'),
    };

    private void RegisterBoundaries(TokenState token, GraphElement activity)
    {
        foreach (var boundary in graph.BoundariesOf(activity.Id))
        {
            if (boundary.EventKind is EventKinds.Timer or EventKinds.Message)
            {
                WaitFor(token, boundary, boundary);
            }
        }
    }

    private void Boundary(TokenState token, GraphElement boundary)
    {
        if (boundary.Interrupting)
        {
            Interrupt(token);
            Record(boundary.AttachedTo!, "interrupted");
        }
        var path = NewToken(boundary.Id, token.Scope, null);
        Record(boundary.Id, "fired");
        Leave(path);
    }

    /// <summary>Stops the token's activity: cancels its waits, any scope it opened, and removes it.</summary>
    private void Interrupt(TokenState token)
    {
        effects.Add(new CancelWaits(token.Id));
        foreach (var scope in state.Scopes.Where(s => s.Owner == token.Id).ToList())
        {
            CloseScope(scope);
        }
        Remove(token);
    }

    private void CloseScope(ScopeState scope)
    {
        foreach (var child in state.Scopes.Where(s => s.Parent == scope.Id).ToList())
        {
            CloseScope(child);
        }
        foreach (var t in state.Tokens.Where(t => t.Scope == scope.Id).ToList())
        {
            effects.Add(new CancelWaits(t.Id));
            foreach (var owned in state.Scopes.Where(s => s.Owner == t.Id).ToList())
            {
                CloseScope(owned);
            }
            Remove(t);
        }
        state.Scopes.Remove(scope);
    }

    private void Leave(TokenState token)
    {
        var element = graph.Element(token.ElementId);
        effects.Add(new CancelWaits(token.Id));
        Remove(token);
        if (token.Body)
        {
            // The activity of a multi-instance body finished: the body scope is done.
            BodyDone(token.Scope);
            return;
        }
        if (element.Outgoing.Count == 0)
        {
            ScopeMaybeDone(token.Scope);
            return;
        }
        foreach (var flow in element.Outgoing)
        {
            Move(token.Scope, flow);
        }
    }

    private void Move(Guid scope, string flowId)
    {
        var flow = graph.Flow(flowId);
        Enqueue(NewToken(flow.Target, scope, flow.Id));
    }

    private void Exclusive(TokenState token, GraphElement gateway)
    {
        string? chosen = null;
        foreach (var flowId in gateway.Outgoing.Where(f => f != gateway.DefaultFlow))
        {
            var flow = graph.Flow(flowId);
            if (flow.Condition is null || FeelEngine.Condition(flow.Condition, Scope(token.Scope), context.Now))
            {
                chosen = flowId;
                break;
            }
        }
        chosen ??= gateway.DefaultFlow;
        if (chosen is null)
        {
            Incident(token, "no_path");
            return;
        }
        Remove(token);
        Record(gateway.Id, "took:" + chosen);
        Move(token.Scope, chosen);
    }

    private void Parallel(TokenState token, GraphElement gateway)
    {
        if (gateway.Incoming.Count > 1)
        {
            token.Status = "joining";
            var arrived = state.Tokens.Where(t => t.ElementId == gateway.Id && t.Scope == token.Scope && t.Status == "joining").ToList();
            var byFlow = gateway.Incoming.Select(f => arrived.FirstOrDefault(t => t.Via == f)).ToList();
            if (byFlow.Any(t => t is null))
            {
                return;
            }
            foreach (var t in byFlow)
            {
                Remove(t!);
            }
            Record(gateway.Id, "joined");
        }
        else
        {
            Remove(token);
        }
        foreach (var flow in gateway.Outgoing)
        {
            Move(token.Scope, flow);
        }
    }

    private void Inclusive(TokenState token, GraphElement gateway)
    {
        if (gateway.Incoming.Count > 1)
        {
            token.Status = "joining";
            var others = state.Tokens.Where(t => t.Scope == token.Scope && t.ElementId != gateway.Id && t.Status != "listening").ToList();
            if (others.Any(t => CanReach(t.ElementId, gateway.Id)) || ready.Any(t => t.Scope == token.Scope && t.ElementId != gateway.Id && CanReach(t.ElementId, gateway.Id)))
            {
                return;
            }
            foreach (var t in state.Tokens.Where(t => t.ElementId == gateway.Id && t.Scope == token.Scope).ToList())
            {
                Remove(t);
            }
            Record(gateway.Id, "joined");
        }
        else
        {
            Remove(token);
        }
        var taken = gateway.Outgoing.Where(f => f != gateway.DefaultFlow && graph.Flow(f).Condition is { } c && FeelEngine.Condition(c, Scope(token.Scope), context.Now)).ToList();
        if (taken.Count == 0 && gateway.Outgoing.Count == 1)
        {
            taken.Add(gateway.Outgoing[0]);
        }
        if (taken.Count == 0 && gateway.DefaultFlow is not null)
        {
            taken.Add(gateway.DefaultFlow);
        }
        if (taken.Count == 0)
        {
            state.Tokens.Add(token);
            Incident(token, "no_path");
            return;
        }
        foreach (var flow in taken)
        {
            Move(token.Scope, flow);
        }
    }

    private bool CanReach(string from, string to)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var queue = new Queue<string>([from]);
        while (queue.TryDequeue(out var id))
        {
            if (id == to)
            {
                return true;
            }
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
        return false;
    }

    private void Throw(TokenState token, GraphElement element)
    {
        switch (element.EventKind)
        {
            case EventKinds.Escalation:
                Record(element.Id, "escalated");
                Leave(token);
                Escalate(null, token.Scope, element.Code!, null);
                return;
            case EventKinds.Message:
                Send(token, element);
                break;
        }
        Leave(token);
    }

    private void Send(TokenState token, GraphElement element)
    {
        var key = element.CorrelationKey is { } k ? FeelText(Eval(k, token.Scope)) : null;
        effects.Add(new SendMessage(element.MessageName!, key, Snapshot(token.Scope)));
        Record(element.Id, "sent");
    }

    private void End(TokenState token, GraphElement end)
    {
        Record(end.Id, "ended");
        if (end.Result is { } result && token.Scope == Guid.Empty)
        {
            state.Result = result;
        }
        switch (end.EventKind)
        {
            case EventKinds.Terminate:
            {
                Remove(token);
                if (token.Scope == Guid.Empty)
                {
                    foreach (var t in state.Tokens.ToList())
                    {
                        effects.Add(new CancelWaits(t.Id));
                    }
                    state.Tokens.Clear();
                    state.Scopes.Clear();
                    Finish("terminated");
                    return;
                }
                var scope = state.Scopes.First(s => s.Id == token.Scope);
                foreach (var child in state.Scopes.Where(s => s.Parent == scope.Id).ToList())
                {
                    CloseScope(child);
                }
                foreach (var t in state.Tokens.Where(t => t.Scope == scope.Id).ToList())
                {
                    effects.Add(new CancelWaits(t.Id));
                    Remove(t);
                }
                ScopeMaybeDone(scope.Id);
                return;
            }
            case EventKinds.Error:
                Remove(token);
                ScopeError(token.Scope, end.Code!, end.Id);
                return;
            case EventKinds.Escalation:
                Remove(token);
                Escalate(null, token.Scope, end.Code!, null);
                ScopeMaybeDone(token.Scope);
                return;
            default:
                Remove(token);
                ScopeMaybeDone(token.Scope);
                return;
        }
    }

    private void Finish(string outcome, string? code = null)
    {
        if (finished)
        {
            return;
        }
        finished = true;
        effects.Add(new Finished(outcome, code, outcome is "completed" or "terminated" ? state.Result : null));
    }

    /// <summary>A scope with no tokens left (listeners aside) is done: its owner moves on, or the instance completes.</summary>
    private void ScopeMaybeDone(Guid scopeId)
    {
        if (finished)
        {
            return;
        }
        if (state.Tokens.Any(t => t.Scope == scopeId && t.Status != "listening") || ready.Any(t => t.Scope == scopeId)
            || state.Scopes.Any(s => s.Parent == scopeId && s.Kind != "listener"))
        {
            return;
        }
        foreach (var listener in state.Tokens.Where(t => t.Scope == scopeId && t.Status == "listening").ToList())
        {
            effects.Add(new CancelWaits(listener.Id));
            Remove(listener);
        }
        if (scopeId == Guid.Empty)
        {
            state.Tokens.Clear();
            Finish("completed");
            return;
        }
        var scope = state.Scopes.First(s => s.Id == scopeId);
        state.Scopes.Remove(scope);
        switch (scope.Kind)
        {
            case "body":
                BodyDone(scope.Id, removed: true, scope);
                return;
            case "event":
                // An interrupting event sub-process replaced the rest of its parent scope; a non-interrupting one ran beside it.
                ScopeMaybeDone(scope.Parent);
                return;
            default:
            {
                var owner = state.Tokens.FirstOrDefault(t => t.Id == scope.Owner);
                if (owner is not null)
                {
                    Record(scope.ElementId, "completed");
                    Leave(owner);
                }
                else
                {
                    ScopeMaybeDone(scope.Parent);
                }
                return;
            }
        }
    }

    // ---- Event sub-processes ----

    private void OpenScope(Guid scopeId)
    {
        var owner = scopeId == Guid.Empty ? null : state.Scopes.First(s => s.Id == scopeId).ElementId;
        foreach (var sub in graph.InScope(owner).Where(e => e.Type == ElementTypes.SubProcess && e.TriggeredByEvent))
        {
            var start = graph.InScope(sub.Id).Single(e => e.Type == ElementTypes.StartEvent);
            if (start.EventKind is EventKinds.Timer or EventKinds.Message)
            {
                var listener = NewToken(start.Id, scopeId, null);
                listener.Status = "listening";
                WaitFor(listener, start, start);
            }
        }
    }

    private void StartEventSubProcess(TokenState listener, GraphElement start)
    {
        var sub = graph.Element(start.Scope!);
        var parent = listener.Scope;
        if (start.Interrupting)
        {
            foreach (var t in state.Tokens.Where(t => t.Scope == parent && t.Id != listener.Id).ToList())
            {
                effects.Add(new CancelWaits(t.Id));
                foreach (var owned in state.Scopes.Where(s => s.Owner == t.Id).ToList())
                {
                    CloseScope(owned);
                }
                Remove(t);
            }
            effects.Add(new CancelWaits(listener.Id));
            Remove(listener);
            foreach (var other in state.Tokens.Where(t => t.Scope == parent && t.Status == "listening").ToList())
            {
                effects.Add(new CancelWaits(other.Id));
                Remove(other);
            }
        }
        var scope = new ScopeState { Id = Guid.NewGuid(), ElementId = sub.Id, Parent = parent, Owner = Guid.Empty, Kind = "event" };
        state.Scopes.Add(scope);
        Record(sub.Id, "triggered");
        Enqueue(NewToken(start.Id, scope.Id, null));
    }

    // ---- Errors and escalations ----

    private void PropagateError(TokenState token, string code)
    {
        var element = graph.Element(token.ElementId);
        var boundary = graph.BoundariesOf(element.Id).Where(b => b.EventKind == EventKinds.Error)
            .OrderBy(b => b.Code is null || b.Code == "" ? 1 : 0).FirstOrDefault(b => string.IsNullOrEmpty(b.Code) || b.Code == code);
        if (boundary is not null)
        {
            Record(element.Id, "error:" + code);
            Boundary(token, boundary);
            return;
        }
        if (token.Body)
        {
            // An error in a multi-instance body ends the loop and goes on from the loop's owner.
            var loop = state.Scopes.First(s => s.Id == state.Scopes.First(b => b.Id == token.Scope).Parent);
            Remove(token);
            var owner = Token(loop.Owner);
            CloseScope(loop);
            PropagateError(owner, code);
            return;
        }
        Interrupt(token);
        ScopeError(token.Scope, code, element.Id);
    }

    /// <summary>An error reached the edge of a scope: an error event sub-process in it, a boundary on its owner, or up to the parent.</summary>
    private void ScopeError(Guid scopeId, string code, string elementId)
    {
        var owner = scopeId == Guid.Empty ? null : state.Scopes.FirstOrDefault(s => s.Id == scopeId);
        var container = owner?.ElementId;
        var handler = graph.InScope(container).Where(e => e.Type == ElementTypes.SubProcess && e.TriggeredByEvent)
            .Select(e => graph.InScope(e.Id).Single(s => s.Type == ElementTypes.StartEvent))
            .FirstOrDefault(s => s.EventKind == EventKinds.Error && (string.IsNullOrEmpty(s.Code) || s.Code == code));
        if (handler is not null)
        {
            var listener = NewToken(handler.Id, scopeId, null);
            listener.Status = "listening";
            StartEventSubProcess(listener, handler with { Interrupting = true });
            return;
        }
        if (owner is null)
        {
            foreach (var t in state.Tokens.ToList())
            {
                effects.Add(new CancelWaits(t.Id));
            }
            state.Tokens.Clear();
            state.Scopes.Clear();
            Finish("error", code);
            return;
        }
        var ownerToken = state.Tokens.FirstOrDefault(t => t.Id == owner.Owner);
        CloseScope(owner);
        if (ownerToken is null)
        {
            ScopeError(owner.Parent, code, elementId);
            return;
        }
        PropagateError(ownerToken, code);
    }

    private void Escalate(string? fromElement, Guid scopeId, string code, TokenState? callToken)
    {
        if (callToken is not null)
        {
            var boundary = graph.BoundariesOf(callToken.ElementId)
                .FirstOrDefault(b => b.EventKind == EventKinds.Escalation && (string.IsNullOrEmpty(b.Code) || b.Code == code));
            if (boundary is not null)
            {
                Boundary(callToken, boundary);
                return;
            }
            scopeId = callToken.Scope;
        }
        for (var current = scopeId; ;)
        {
            var scope = current == Guid.Empty ? null : state.Scopes.FirstOrDefault(s => s.Id == current);
            var handler = graph.InScope(scope?.ElementId).Where(e => e.Type == ElementTypes.SubProcess && e.TriggeredByEvent)
                .Select(e => graph.InScope(e.Id).Single(s => s.Type == ElementTypes.StartEvent))
                .FirstOrDefault(s => s.EventKind == EventKinds.Escalation && (string.IsNullOrEmpty(s.Code) || s.Code == code));
            if (handler is not null)
            {
                var listener = NewToken(handler.Id, current, null);
                listener.Status = "listening";
                StartEventSubProcess(listener, handler);
                if (!handler.Interrupting)
                {
                    Remove(listener);
                }
                return;
            }
            if (scope is null)
            {
                effects.Add(new Escalated(code));
                return;
            }
            var owner = state.Tokens.FirstOrDefault(t => t.Id == scope.Owner);
            if (owner is not null)
            {
                var boundary = graph.BoundariesOf(owner.ElementId)
                    .FirstOrDefault(b => b.EventKind == EventKinds.Escalation && (string.IsNullOrEmpty(b.Code) || b.Code == code));
                if (boundary is not null)
                {
                    Boundary(owner, boundary);
                    return;
                }
            }
            current = scope.Parent;
        }
    }

    // ---- Multi-instance ----

    private void StartLoop(TokenState token, GraphElement element)
    {
        var loop = element.Loop!;
        var collection = Eval(loop.Collection, token.Scope);
        if (collection is not IList<object?> items)
        {
            Incident(token, "loop_collection");
            return;
        }
        if (items.Count > 1000)
        {
            Incident(token, "loop_collection");
            return;
        }
        RegisterBoundaries(token, element);
        Record(element.Id, "loop_started", new JsonObject { ["count"] = items.Count });
        if (items.Count == 0)
        {
            Record(element.Id, "completed");
            Leave(token);
            return;
        }
        var scope = new ScopeState
        {
            Id = Guid.NewGuid(), ElementId = element.Id, Parent = token.Scope, Owner = token.Id, Kind = "loop",
            Items = items.Select(FeelEngine.ToJson).ToList(), Sequential = loop.Sequential,
        };
        state.Scopes.Add(scope);
        var first = loop.Sequential ? 1 : items.Count;
        for (var i = 0; i < first; i++)
        {
            StartBody(scope, element);
        }
    }

    private void StartBody(ScopeState loop, GraphElement element)
    {
        var index = loop.Next++;
        var body = new ScopeState
        {
            Id = Guid.NewGuid(), ElementId = element.Id, Parent = loop.Id, Owner = Guid.Empty, Kind = "body",
            Locals = new JsonObject { [element.Loop!.ElementVariable] = loop.Items![index]?.DeepClone(), ["loopCounter"] = index + 1 },
        };
        state.Scopes.Add(body);
        Enqueue(NewToken(element.Id, body.Id, null, body: true));
    }

    private void BodyDone(Guid bodyId, bool removed = false, ScopeState? body = null)
    {
        body ??= state.Scopes.FirstOrDefault(s => s.Id == bodyId);
        if (body is null)
        {
            return;
        }
        // A sub-process body still has work in its own scope; wait for it.
        if (!removed && (state.Tokens.Any(t => t.Scope == bodyId) || state.Scopes.Any(s => s.Parent == bodyId)))
        {
            return;
        }
        state.Scopes.Remove(body);
        var loop = state.Scopes.FirstOrDefault(s => s.Id == body.Parent);
        if (loop is null)
        {
            return;
        }
        loop.Completed++;
        var element = graph.Element(loop.ElementId);
        var total = loop.Items!.Count;
        var vars = Scope(loop.Parent);
        vars["nrOfInstances"] = (decimal)total;
        vars["nrOfCompletedInstances"] = (decimal)loop.Completed;
        vars["nrOfActiveInstances"] = (decimal)state.Scopes.Count(s => s.Parent == loop.Id);
        var done = loop.Completed >= total
            || element.Loop!.CompletionCondition is { } condition && FeelEngine.Condition(condition, vars, context.Now);
        if (!done)
        {
            if (loop.Sequential && loop.Next < total)
            {
                StartBody(loop, element);
            }
            return;
        }
        var owner = state.Tokens.FirstOrDefault(t => t.Id == loop.Owner);
        CloseScope(loop);
        Record(element.Id, "completed", new JsonObject { ["instances"] = loop.Completed });
        if (owner is not null)
        {
            Leave(owner);
        }
    }

    // ---- Variables and incidents ----

    private void Apply(GraphElement element, JsonObject result, TokenState token)
    {
        var mappings = element.Type == ElementTypes.ServiceTask ? element.Call?.Outputs : element.Outputs;
        if (element.Type == ElementTypes.UserTask)
        {
            var tasks = state.Variables["tasks"] as JsonObject ?? new JsonObject();
            tasks[element.Id] = result.DeepClone();
            state.Variables["tasks"] = tasks;
            if (result["outcome"] is { } outcome)
            {
                state.Variables["outcome"] = outcome.DeepClone();
            }
        }
        if (mappings is { Count: > 0 })
        {
            var vars = Scope(token.Scope);
            vars["result"] = FeelEngine.FromJson(result);
            foreach (var (variable, source) in mappings)
            {
                state.Variables[variable] = FeelEngine.ToJson(FeelEngine.Evaluate(source, vars, context.Now));
            }
        }
        else if (element.Type is not (ElementTypes.UserTask or ElementTypes.CallActivity))
        {
            foreach (var (key, value) in result)
            {
                state.Variables[key] = value?.DeepClone();
            }
        }
    }

    /// <summary>A message or timer payload: mapped by the event's outputs if it has them, else merged into the variables.</summary>
    private void Merge(GraphElement element, JsonObject payload)
    {
        foreach (var (key, value) in payload)
        {
            state.Variables[key] = value?.DeepClone();
        }
    }

    private void Incident(TokenState token, string errorClass, string? code = null)
    {
        if (!state.Tokens.Contains(token))
        {
            state.Tokens.Add(token);
        }
        token.Status = "incident";
        effects.Add(new RaiseIncident(token.Id, token.ElementId, errorClass, code));
        Record(token.ElementId, "incident", new JsonObject { ["class"] = errorClass });
    }
}

/// <summary>A decision that cannot be evaluated: missing, no rule matched under a policy that needs one, or a broken cell.</summary>
public sealed class DecisionException(string code) : Exception(code)
{
    public string Code { get; } = code;
}
