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
/// see the tool's title and description from the web message catalogue, keyed by the tool name.
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
    string? RequiredCapability = null);

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
