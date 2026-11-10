using TeamBeats.Primitives;

namespace TeamBeats.Modules.Agents.Contracts;

/// <summary>What an assistant turn ended with (spec 014 FR-011). The web app shows a message for each.</summary>
public static class AssistantOutcomes
{
    /// <summary>The model answered from permitted passages.</summary>
    public const string Answered = "answered";

    /// <summary>No permitted evidence: nothing was sent to a model (US1 scenario 2).</summary>
    public const string NoEvidence = "no_evidence";

    /// <summary>Evidence exists but the AI policy keeps it from the assistant (US1 scenario 3).</summary>
    public const string PolicyBlocked = "policy_blocked";

    /// <summary>The gateway gave no answer; the turn's reason is the gateway denial (spec 013 FR-016) or <c>refused</c>.</summary>
    public const string Unavailable = "unavailable";

    public static readonly IReadOnlyList<string> All = [Answered, NoEvidence, PolicyBlocked, Unavailable];

    /// <summary>The agent the turn was routed to proposed an action and waits for the person to confirm it (spec 014 US2 scenario 2).</summary>
    public const string ActionPending = "action_pending";
}

/// <summary>An assistant turn finished (spec 014 section 12), for analytics. It carries IDs and the outcome, never the text.</summary>
public sealed record AssistantAnswered(Guid TurnId, Guid ConversationId, Guid UserId, string? Agent, string Outcome, int Citations) : IIntegrationEvent;

/// <summary>What a tool changes (spec 027 FR-005). Read tools change nothing; drafts stay with the person until they act.</summary>
public static class ToolSideEffects
{
    public const string Read = "read";
    public const string Draft = "draft";
    public const string WriteSelf = "write_self";
    public const string WriteExternal = "write_external";

    public static readonly IReadOnlyList<string> All = [Read, Draft, WriteSelf, WriteExternal];

    public static bool IsWrite(string sideEffect) => sideEffect is WriteSelf or WriteExternal;
}

/// <summary>
/// What must happen before a tool call takes effect (spec 027 US4). Ordered from least to most strict: a tenant may only
/// move a tool to a stricter rule, and <see cref="Block"/> tools can never be held by an agent.
/// </summary>
public static class ToolApprovalRules
{
    public const string Execute = "execute";
    public const string DraftOnly = "draft_only";
    public const string Confirm = "confirm";
    public const string Approval = "approval";
    public const string Block = "block";

    public static readonly IReadOnlyList<string> Ordered = [Execute, DraftOnly, Confirm, Approval, Block];

    /// <summary>The rule's place in <see cref="Ordered"/>; an unknown rule counts as stricter than all.</summary>
    public static int Strictness(string rule)
    {
        for (var i = 0; i < Ordered.Count; i++)
        {
            if (string.Equals(Ordered[i], rule, StringComparison.Ordinal))
            {
                return i;
            }
        }
        return Ordered.Count;
    }
}

/// <summary>
/// A tool in the registry (spec 027 FR-005), declared in code by the module that owns the action. <paramref name="Parameters"/>
/// is the JSON schema of the arguments the model may pass; <paramref name="OutputClassification"/> is the highest
/// classification of what the tool can return. <paramref name="Description"/> tells the model what the tool does; people
/// see the tool's title and description from the web message catalogue, keyed by the tool name, or, for a tenant tool,
/// the <paramref name="Title"/> per language its administrator entered.
/// </summary>
public sealed record ToolDefinition(
    string Name,
    string Module,
    int Version,
    string SideEffect,
    string ApprovalRule,
    System.Text.Json.JsonElement Parameters,
    string OutputClassification,
    bool Idempotent,
    int TimeoutSeconds,
    string Description,
    string? RequiredCapability = null,
    IReadOnlyDictionary<string, string>? Title = null);

/// <summary>
/// Who a tool call runs for: the person, through an agent, in a run. Authorization is the person's at the time of the
/// call. <paramref name="MaxClassification"/> is the agent's data cap: a tool leaves out anything above it (spec 027
/// FR-017). <paramref name="IdempotencyKey"/> is the hash of the call, the same on a retry. <paramref name="PassagesSoFar"/>
/// is how many passages the run has numbered already, so citations stay unique across tool calls.
/// </summary>
public sealed record ToolCallContext(Guid RunId, Guid AgentId, int AgentVersion, Guid UserId, bool DryRun, string MaxClassification = "internal",
    string? IdempotencyKey = null, int PassagesSoFar = 0);

/// <summary>A source a tool read, for citations and the execution record.</summary>
public sealed record ToolSource(string Source, Guid ItemId, string Title, string Link, string Classification);

/// <summary>
/// What a tool gives back. <paramref name="Content"/> goes to the model inside the untrusted envelope; <paramref name="ErrorKey"/>
/// is a short, safe reason when it failed, never internal detail.
/// </summary>
public sealed record ToolResult(bool Ok, string Content, string Classification, IReadOnlyList<ToolSource> Sources, string? ErrorKey = null)
{
    public static ToolResult Failed(string errorKey) => new(false, string.Empty, "public", [], errorKey);
}

/// <summary>
/// A tool a module offers to agents (spec 027 plan: modules register tools through <c>Agents.Contracts</c>). The handler
/// calls the owning module's contracts under the person's authorization; it never checks agent policy itself, which the
/// tool gateway does before it is called.
/// </summary>
public interface IAgentTool
{
    ToolDefinition Definition { get; }

    Task<ToolResult> ExecuteAsync(System.Text.Json.JsonElement arguments, ToolCallContext context, CancellationToken ct);
}

/// <summary>
/// Tenant tools (spec 027 FR-006, R2): tools a tenant registered itself, such as connector actions of spec 017, as
/// opposed to the product tools declared in code. Asked once per scope, in the current tenant. Names must not clash with
/// product tools; a source keeps its own names under a prefix such as <c>connector.</c>.
/// </summary>
public interface IAgentToolSource
{
    Task<IReadOnlyList<IAgentTool>> ToolsAsync(CancellationToken ct);
}

/// <summary>
/// What other modules may know about an agent (spec 034 evaluates agents): its owning unit, owner, template, state,
/// risk and the languages of its latest version. The caller checks its own permissions on <see cref="OrgUnitId"/>.
/// </summary>
public sealed record AgentFacts(
    Guid Id,
    Guid OrgUnitId,
    Guid OwnerUserId,
    string? TemplateId,
    string Status,
    string RiskClass,
    int? CurrentVersion,
    int LatestVersion,
    IReadOnlyList<string> Languages,
    IReadOnlyDictionary<string, string>? Name);

public interface IAgentCatalog
{
    Task<AgentFacts?> GetAsync(Guid agentId, CancellationToken ct = default);

    Task<AgentVersionFacts?> VersionAsync(Guid agentId, int number, CancellationToken ct = default);

    /// <summary>Every agent of the tenant that is not retired, without a permission check; callers filter by scope.</summary>
    Task<IReadOnlyList<AgentFacts>> ListAsync(CancellationToken ct = default);

    /// <summary>The agents in use now: published, with a version in force.</summary>
    Task<IReadOnlyList<AgentFacts>> PublishedAsync(CancellationToken ct = default);
}

/// <summary>A step of an evaluation run's execution record (spec 034 US3 scenario 5): what the agent did, in order.</summary>
public sealed record EvalRunStep(int N, string Kind, string? Tool, string Decision, string? Reason, System.Text.Json.JsonElement? Parameters);

/// <summary>A source the agent read in an evaluation run, numbered in the order it was read.</summary>
public sealed record EvalRunSource(int N, string Source, Guid ItemId);

/// <summary>
/// What one evaluation case produced on the real runtime (spec 034 FR-007): the outcome, the answer, the ordered steps,
/// every source the agent read, the ones its answer cited, and the model, tokens and time it took.
/// </summary>
public sealed record EvalRunRecord(
    Guid RunId,
    string Status,
    string? Outcome,
    string? Reason,
    string? Answer,
    string Language,
    string? Model,
    int InputTokens,
    int OutputTokens,
    int DurationMs,
    IReadOnlyList<EvalRunStep> Steps,
    IReadOnlyList<EvalRunSource> Read,
    IReadOnlyList<EvalRunSource> Cited,
    string? Escalation);

/// <summary>Step decisions an evaluation reads from the execution record (spec 027 section 13).</summary>
public static class EvalStepDecisions
{
    public const string Executed = "executed";
    public const string Refused = "refused";
    public const string ConfirmationRequested = "confirmation_requested";
    public const string Confirmed = "confirmed";

    /// <summary>A write tool in an evaluation run: the call was recorded and a sandbox double answered, so nothing changed.</summary>
    public const string Sandboxed = "sandboxed";
}

/// <summary>
/// Runs one evaluation case on the production runtime (spec 034 US3 scenario 1, ADR-0011): the given agent version, as
/// the person in context (a synthetic test identity), through the AI Gateway and the tool gateway, with every write
/// tool bound to a recording sandbox double. The run is kept as a normal execution record marked <c>eval</c>. The caller
/// checks its own permission to evaluate the agent; a suspended or retired agent, or the kill switch, is a conflict.
/// </summary>
public interface IAgentEvaluationRuntime
{
    /// <param name="routeChangeId">For a release gate run of a proposed model route change: the agent's model calls follow the proposed routes.</param>
    Task<EvalRunRecord> RunCaseAsync(Guid agentId, int version, string message, string language, Guid? routeChangeId = null, CancellationToken ct = default);
}

/// <summary>A synthetic passage an assistant evaluation case gives the knowledge agent in place of retrieval (spec 034, spec 014 T008).</summary>
public sealed record EvalPassage(string Title, string Text);

/// <summary>
/// Runs one assistant evaluation case (spec 034 FR-004, spec 014 FR-013): the knowledge agent's prompt, envelope and
/// citation check of spec 014 over the case's synthetic passages, as the person in context (a synthetic test identity),
/// through the AI Gateway on the assistant use case. No conversation or turn is stored. The passages are numbered in
/// the order given and come back as <see cref="EvalRunRecord.Read"/> with source <see cref="Source"/> and an item ID
/// derived from their position, so citations can be checked. The assistant capability being off is a conflict.
/// </summary>
public interface IAssistantEvaluationRuntime
{
    public const string Source = "eval_passage";

    /// <param name="routeChangeId">For a release gate run of a proposed model route change: the call follows the proposed routes.</param>
    Task<EvalRunRecord> RunCaseAsync(string question, string language, IReadOnlyList<EvalPassage> passages, Guid? routeChangeId = null, CancellationToken ct = default);

    /// <summary>The item ID of the passage numbered <paramref name="n"/> (from 1) in a case's record.</summary>
    static Guid PassageId(int n) => new($"00000000-0000-0000-0000-{n:D12}");
}

/// <summary>Where the release gate of a version stands (spec 034 US5, spec 020 US5).</summary>
public static class GateStatuses
{
    public const string NotBuilt = "not_built";
    public const string NotRun = "not_run";
    public const string Running = "running";
    public const string Passed = "passed";
    public const string Failed = "failed";
    public const string FailedCritical = "failed_critical";
    public const string Incomplete = "incomplete";
    public const string Overridden = "overridden";
    public const string Stale = "stale";
    public const string Cancelled = "cancelled";
}

/// <summary>A metric of the version under review next to the published version's, in one locale (US5 scenario 1).</summary>
public sealed record GateMetricDiff(string Metric, string Locale, double? Published, double? Value);

/// <summary>
/// The release gate result of a version as the review page shows it (spec 034 US5): the status, whether it blocks
/// publishing, the failing cases, the failing metrics by severity, the score differences against the published
/// version, and whether the person asking may override a blocking failure.
/// </summary>
public sealed record AgentGateResult(
    string Status,
    bool Blocking,
    IReadOnlyList<string> FailingCases,
    Guid? RunId = null,
    bool CanOverride = false,
    IReadOnlyList<string>? CriticalFailures = null,
    IReadOnlyList<string>? BlockingFailures = null,
    IReadOnlyList<string>? Incomplete = null,
    IReadOnlyList<GateMetricDiff>? Diff = null,
    string? OverrideReason = null);

/// <summary>
/// The release gate of spec 034 as the Agent Hub uses it (spec 020 FR-009): a submitted version asks for a gate run,
/// and publishing reads the result. The evaluation module implements it.
/// </summary>
public interface IAgentReleaseGate
{
    Task RequestAsync(Guid agentId, int version, CancellationToken ct);

    Task<AgentGateResult> ResultAsync(Guid agentId, int version, CancellationToken ct);
}

/// <summary>What other modules may know about one version of an agent: its state, risk class and last editor (spec 034 overrides).</summary>
public sealed record AgentVersionFacts(int Number, string State, string RiskClass, Guid LastEditor);

/// <summary>The risk classes of an agent version (spec 020 FR-006).</summary>
public static class AgentRiskClasses
{
    public const string Low = "low";
    public const string Medium = "medium";
    public const string High = "high";
}
