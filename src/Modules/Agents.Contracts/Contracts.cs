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
