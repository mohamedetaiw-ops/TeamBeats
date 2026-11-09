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
