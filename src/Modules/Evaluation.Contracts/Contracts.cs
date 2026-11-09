namespace TeamBeats.Modules.Evaluation.Contracts;

/// <summary>Threshold severities of spec 034 FR-014: critical blocks with no override, blocking may be overridden, warning only reports.</summary>
public static class EvalSeverities
{
    public const string Critical = "critical";
    public const string Blocking = "blocking";
    public const string Warning = "warning";

    public static readonly IReadOnlyList<string> All = [Warning, Blocking, Critical];

    /// <summary>Higher is stricter.</summary>
    public static int Rank(string severity) => severity switch { Critical => 3, Blocking => 2, Warning => 1, _ => 0 };
}

/// <summary>How a metric is computed (section 12): deterministic and trajectory checks need no model, judged ones call a judge, measured ones read run records.</summary>
public static class EvalMethods
{
    public const string Deterministic = "deterministic";
    public const string Trajectory = "trajectory";
    public const string Judged = "judged";
    public const string Measured = "measured";
}

/// <summary>What a finished evaluation run concluded (spec 034 US4, FR-017): passed, failed, or incomplete when it could not be scored in full.</summary>
public static class EvalVerdicts
{
    public const string Passed = "passed";
    public const string Failed = "failed";
    public const string Incomplete = "incomplete";
}

/// <summary>A run was queued or has cases left; the evaluation worker runs the next batch (spec 034 plan: runs on workers through the outbox).</summary>
public sealed record EvaluationRunRequested(Guid RunId) : TeamBeats.Primitives.IIntegrationEvent;

/// <summary>An evaluation run finished (spec 034 section 13). It carries IDs and the verdict, never case content.</summary>
public sealed record EvaluationRunCompleted(Guid RunId, Guid AgentId, int AgentVersion, string Status, string? Verdict) : TeamBeats.Primitives.IIntegrationEvent;

/// <summary>A release gate decided for an agent version (spec 034 section 13): passed, failed or incomplete, and later overridden.</summary>
public sealed record EvaluationGateDecided(Guid RunId, Guid AgentId, int AgentVersion, string Result) : TeamBeats.Primitives.IIntegrationEvent;

/// <summary>
/// The release gate decided a proposed model route change (spec 034 US5 scenario 4, spec 013 FR-019): every published
/// agent on the changed use cases ran against the proposed routes. <paramref name="Result"/> is passed, failed (blocking
/// failures only), incomplete or failed_critical; <paramref name="Failures"/> names them as "agentId/metric:locale".
/// </summary>
public sealed record EvaluationRouteGateDecided(Guid RouteChangeId, string Result, IReadOnlyList<string> Failures) : TeamBeats.Primitives.IIntegrationEvent;
