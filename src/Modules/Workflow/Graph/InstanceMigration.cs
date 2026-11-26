namespace TeamBeats.Modules.Workflow.Graph;

/// <summary>One wait an instance has open (a timer or a message subscription), by the event element it waits for.</summary>
public sealed record OpenWait(Guid Token, string EventElementId);

/// <summary>Whether one instance can move to the target version, and if not, why and at which element.</summary>
public sealed record MigrationCheck(bool Migratable, string? Reason = null, string? ElementId = null)
{
    public static readonly MigrationCheck Ok = new(true);
}

/// <summary>
/// Moves the state of a running instance from one version of a process to another (spec 024 R3, FR-015, US6). The mapping
/// names, for elements of the source version, the element of the target version they become; an element without an
/// entry keeps its ID when the target has it. An instance moves only when every place it is at maps to an element of
/// the same kind, every wait it has open still belongs to that element, and every sub-process or multi-instance it is
/// inside has the same structure in both versions (scenario 4). Nothing else of the instance changes: variables,
/// history and open tasks stay, re-linked to the new element IDs.
/// </summary>
public static class InstanceMigration
{
    public const string Unmapped = "unmapped";
    public const string KindChanged = "kind_changed";
    public const string StructureChanged = "structure_changed";
    public const string WaitChanged = "wait_changed";
    public const string JoinChanged = "join_changed";
    public const string Compensating = "compensating";
    public const string CompensationChanged = "compensation_changed";

    /// <summary>The proposal for a plan (scenario 1): same ID and same kind map to themselves; the rest is left for the operator.</summary>
    public static IReadOnlyDictionary<string, string?> Propose(ProcessGraph from, ProcessGraph to)
    {
        var proposal = new SortedDictionary<string, string?>(StringComparer.Ordinal);
        foreach (var e in from.Elements)
        {
            proposal[e.Id] = to.Has(e.Id) && SameKind(e, to.Element(e.Id)) ? e.Id : null;
        }
        return proposal;
    }

    /// <summary>The target element of a source element, or null when it has none.</summary>
    public static string? Target(ProcessGraph to, IReadOnlyDictionary<string, string> mapping, string elementId) =>
        mapping.TryGetValue(elementId, out var mapped) ? (to.Has(mapped) ? mapped : null) : to.Has(elementId) ? elementId : null;

    public static MigrationCheck Check(ProcessGraph from, ProcessGraph to, IReadOnlyDictionary<string, string> mapping, InstanceState state, IReadOnlyList<OpenWait> waits)
    {
        foreach (var scope in state.Scopes)
        {
            if (scope.Kind == "compensation")
            {
                // Handlers are running: moving now could undo against the other version's steps.
                return new MigrationCheck(false, Compensating, scope.ElementId);
            }
            var target = Target(to, mapping, scope.ElementId);
            if (target != scope.ElementId || !SameStructure(from, to, scope.ElementId))
            {
                return new MigrationCheck(false, StructureChanged, scope.ElementId);
            }
        }
        foreach (var token in state.Tokens)
        {
            var source = from.Element(token.ElementId);
            if (token.Status == "compensating")
            {
                return new MigrationCheck(false, Compensating, token.ElementId);
            }
            if (Target(to, mapping, token.ElementId) is not { } target)
            {
                return new MigrationCheck(false, Unmapped, token.ElementId);
            }
            var mapped = to.Element(target);
            if (!SameKind(source, mapped))
            {
                return new MigrationCheck(false, KindChanged, token.ElementId);
            }
            if (token.Scope != Guid.Empty && target != token.ElementId)
            {
                return new MigrationCheck(false, StructureChanged, token.ElementId);
            }
            if (token.Status == "joining" && (token.Via is not { } via || !to.Flows.Any(f => f.Id == via && f.Target == target)))
            {
                return new MigrationCheck(false, JoinChanged, token.ElementId);
            }
        }
        foreach (var wait in waits)
        {
            if (!from.Has(wait.EventElementId) || Target(to, mapping, wait.EventElementId) is not { } target || !SameKind(from.Element(wait.EventElementId), to.Element(target)))
            {
                return new MigrationCheck(false, WaitChanged, wait.EventElementId);
            }
            var source = from.Element(wait.EventElementId);
            var into = to.Element(target);
            if (source.MessageName != into.MessageName || source.CorrelationKey != into.CorrelationKey)
            {
                // A subscription is matched by name and key, so a changed message would never arrive.
                return new MigrationCheck(false, WaitChanged, wait.EventElementId);
            }
            if (source.Type == ElementTypes.BoundaryEvent && Target(to, mapping, source.AttachedTo!) != to.Element(target).AttachedTo)
            {
                return new MigrationCheck(false, WaitChanged, wait.EventElementId);
            }
        }
        foreach (var done in state.Compensable)
        {
            if (Target(to, mapping, done.ElementId) is not { } target || to.CompensationOf(target) is null)
            {
                return new MigrationCheck(false, CompensationChanged, done.ElementId);
            }
        }
        return MigrationCheck.Ok;
    }

    /// <summary>Re-points the state to the target version's element IDs. Call only after <see cref="Check"/> passed.</summary>
    public static void Apply(ProcessGraph to, IReadOnlyDictionary<string, string> mapping, InstanceState state)
    {
        foreach (var token in state.Tokens)
        {
            token.ElementId = Target(to, mapping, token.ElementId)!;
        }
        foreach (var done in state.Compensable)
        {
            done.ElementId = Target(to, mapping, done.ElementId)!;
        }
        var visits = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var (id, count) in state.Visits)
        {
            if (Target(to, mapping, id) is { } target)
            {
                visits[target] = Math.Max(count, visits.GetValueOrDefault(target));
            }
        }
        state.Visits = visits;
    }

    /// <summary>Same element type and, for events, the same trigger: a user task stays a user task, a timer boundary a timer boundary.</summary>
    public static bool SameKind(GraphElement a, GraphElement b) =>
        a.Type == b.Type && a.EventKind == b.EventKind && (a.Loop is null) == (b.Loop is null) && a.TriggeredByEvent == b.TriggeredByEvent;

    /// <summary>A sub-process or multi-instance activity whose inside is the same in both versions: the same elements, kinds, flows and loop.</summary>
    private static bool SameStructure(ProcessGraph from, ProcessGraph to, string elementId)
    {
        if (!to.Has(elementId) || !SameKind(from.Element(elementId), to.Element(elementId)) || from.Element(elementId).Loop != to.Element(elementId).Loop)
        {
            return false;
        }
        static IEnumerable<string> Inside(ProcessGraph g, string scope)
        {
            foreach (var e in g.InScope(scope))
            {
                yield return $"{e.Id}|{e.Type}|{e.EventKind}|{e.AttachedTo}|{e.Loop is not null}|{e.TriggeredByEvent}|{string.Join(',', e.Outgoing)}";
                foreach (var inner in Inside(g, e.Id))
                {
                    yield return inner;
                }
            }
        }
        IEnumerable<string> Flows(ProcessGraph g) =>
            Inside(g, elementId).Select(s => s.Split('|')[0]).SelectMany(id => g.Element(id).Outgoing).Select(f => g.Flow(f)).Select(f => $"{f.Id}|{f.Source}|{f.Target}|{f.Condition}");
        return Inside(from, elementId).Order(StringComparer.Ordinal).SequenceEqual(Inside(to, elementId).Order(StringComparer.Ordinal))
            && Flows(from).Order(StringComparer.Ordinal).SequenceEqual(Flows(to).Order(StringComparer.Ordinal));
    }
}
