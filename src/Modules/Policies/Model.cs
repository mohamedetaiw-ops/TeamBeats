using System.Text.Json;
using System.Text.Json.Serialization;
using TeamBeats.Modules.Content.Contracts;

namespace TeamBeats.Modules.Policies;

/// <summary>Where a policy is in its lifecycle (spec 025 section 2). Under review is R2 with scheduled reviews.</summary>
public static class PolicyStatuses
{
    public const string Draft = "draft";
    public const string InReview = "in_review";
    public const string Approved = "approved";
    public const string Published = "published";
    public const string Retired = "retired";
}

/// <summary>
/// States of a version. A draft goes in review; final approval gives it a number and makes it approved; on its effective
/// date it becomes effective and the one before it superseded. A returned version is a draft again.
/// </summary>
public static class VersionStates
{
    public const string Draft = "draft";
    public const string InReview = "in_review";
    public const string Approved = "approved";
    public const string Effective = "effective";
    public const string Superseded = "superseded";
}

public static class Securables
{
    public const string Policy = "policy";
}

/// <summary>
/// One approval step of a policy's route: named approvers, or the compliance officers who hold <c>policy.manage</c>
/// at the policy's unit. Names per locale label the step in the inbox and the approval trail.
/// </summary>
public sealed record RouteStepSetting(IReadOnlyDictionary<string, string>? Names = null, IReadOnlyList<Guid>? Approvers = null, bool ComplianceOfficers = false);

public sealed record PolicyRequest(
    string? Code,
    IReadOnlyDictionary<string, string>? Names,
    Guid? CategoryId,
    Guid? OwnerId,
    Guid? OrgUnitId,
    ContentAudience? Audience,
    string? Classification,
    string? BindingLocale,
    IReadOnlyList<RouteStepSetting>? Route,
    int? ReviewMonths,
    IReadOnlyList<Guid>? Related);

public sealed record CategoryRequest(IReadOnlyDictionary<string, string>? Names);

public sealed record CategoryView(Guid Id, IReadOnlyDictionary<string, string> Names);

/// <summary>
/// A clause as the editor sends it. <see cref="Key"/> is the clause's stable identity: send back the key of a clause
/// that already exists so its anchor and comments follow it; leave it out for a new clause. Level 1 to 3 gives the
/// numbering (4, 4.2, 4.2.1). Title and body are given for every locale of the version.
/// </summary>
public sealed record ClauseInput(string? Key, int? Level, IReadOnlyDictionary<string, string>? Titles, IReadOnlyDictionary<string, BodyDocument>? Bodies);

public sealed record DraftRequest(IReadOnlyDictionary<string, string>? Titles, IReadOnlyList<ClauseInput>? Clauses, bool? Material, string? ChangeNote);

public sealed record SubmitRequest(DateOnly? EffectiveOn);

public sealed record CommentRequest(string? ClauseKey, string? Text);

public sealed record PolicyPerson(Guid Id, string DisplayName);

/// <summary>A clause with its number and anchor worked out from its place in the version.</summary>
public sealed record ClauseView(string Key, string Anchor, string Number, int Level, IReadOnlyDictionary<string, string> Titles, IReadOnlyDictionary<string, BodyDocument> Bodies);

/// <summary>One approval in the trail: the step, who approved and when, and whom a delegate acted for.</summary>
public sealed record ApprovalView(string Step, IReadOnlyDictionary<string, string>? StepNames, PolicyPerson Approver, PolicyPerson? OnBehalfOf, DateTimeOffset At);

public sealed record VersionView(
    Guid Id,
    int? Number,
    string State,
    IReadOnlyList<string> Locales,
    IReadOnlyDictionary<string, string> Titles,
    IReadOnlyList<ClauseView> Clauses,
    bool Material,
    string? ChangeNote,
    IReadOnlyList<ApprovalView> Approvals,
    DateTimeOffset? RequestedEffectiveFrom,
    DateTimeOffset? EffectiveFrom,
    DateTimeOffset? SubmittedAt,
    DateTimeOffset? ApprovedAt,
    DateTimeOffset? SupersededAt,
    DateTimeOffset UpdatedAt);

/// <summary>
/// A version for a reader (US1 scenarios 4 and 5): the policy, the binding language for the note on other languages,
/// and, on a superseded version, the number of the version in force for the banner.
/// </summary>
public sealed record PolicyReading(PolicySummary Policy, VersionView Version, bool Superseded, int? CurrentVersion, string? BindingLocale);

public sealed record PolicySummary(Guid Id, string Code, IReadOnlyDictionary<string, string> Names, Guid? CategoryId, string Status, string Classification, PolicyPerson Owner);

public sealed record WorkingVersion(Guid Id, string State, DateTimeOffset? RequestedEffectiveFrom, DateTimeOffset? SubmittedAt, DateTimeOffset UpdatedAt);

public sealed record ScheduledVersion(Guid Id, int Number, DateTimeOffset EffectiveFrom);

/// <summary>What the current user may do with a policy, so the page shows the right actions; the API checks again.</summary>
public sealed record PolicyRights(bool Manage, bool Edit, bool Review, bool History);

public sealed record PolicyView(
    Guid Id,
    string Code,
    IReadOnlyDictionary<string, string> Names,
    Guid? CategoryId,
    PolicyPerson Owner,
    Guid? OrgUnitId,
    ContentAudience Audience,
    string Classification,
    string? BindingLocale,
    IReadOnlyList<RouteStepSetting> Route,
    int ReviewMonths,
    IReadOnlyList<Guid> Related,
    string Status,
    int? CurrentVersion,
    DateTimeOffset? EffectiveFrom,
    DateTimeOffset? NextReviewAt,
    WorkingVersion? Working,
    ScheduledVersion? Scheduled,
    PolicyRights Rights,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record PolicyListItem(
    Guid Id,
    string Code,
    IReadOnlyDictionary<string, string> Names,
    Guid? CategoryId,
    PolicyPerson Owner,
    Guid? OrgUnitId,
    string Classification,
    string Status,
    int? CurrentVersion,
    DateTimeOffset? EffectiveFrom,
    DateTimeOffset? NextReviewAt,
    string? WorkingState);

public sealed record VersionHistoryItem(Guid Id, int Number, string State, bool Material, string? ChangeNote, DateTimeOffset? EffectiveFrom, DateTimeOffset? SupersededAt,
    IReadOnlyList<ApprovalView> Approvals);

/// <summary>How one clause changed between the version in force and the version compared with it.</summary>
public static class ClauseChanges
{
    public const string Added = "added";
    public const string Removed = "removed";
    public const string Changed = "changed";
    public const string Unchanged = "unchanged";
}

public sealed record ClauseChange(string Key, string Change, ClauseView? Before, ClauseView? After);

public sealed record CompareView(int? Base, Guid VersionId, int? Number, IReadOnlyList<ClauseChange> Clauses, bool TitlesChanged);

public sealed record CommentView(Guid Id, Guid VersionId, string ClauseKey, PolicyPerson Author, string Text, DateTimeOffset CreatedAt, DateTimeOffset? ResolvedAt,
    PolicyPerson? ResolvedBy);

/// <summary>A clause as stored in the version row: its key, level and text per locale.</summary>
internal sealed record StoredClause(string Key, int Level, Dictionary<string, string> Titles, Dictionary<string, BodyDocument> Bodies);

/// <summary>An approval as stored in the version row.</summary>
internal sealed record StoredApproval(string Step, Dictionary<string, string>? StepNames, Guid UserId, Guid? OnBehalfOf, DateTimeOffset At);

/// <summary>JSON for stored policy data: camelCase, nulls left out so bodies stay small and stable for hashes.</summary>
internal static class PolicyJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    public static T Read<T>(string json) => JsonSerializer.Deserialize<T>(json, Options)!;

    public static string Write<T>(T value) => JsonSerializer.Serialize(value, Options);
}

public static class MemberStatuses
{
    public const string Open = "open";
    public const string Attested = "attested";
    public const string Overdue = "overdue";
    public const string LeftAudience = "left_audience";
}

public static class AttestationChannels
{
    public const string Web = "web";
    public const string Mobile = "mobile";

    public static readonly IReadOnlySet<string> Online = new HashSet<string>(StringComparer.Ordinal) { Web, Mobile };
}

/// <summary>
/// A campaign launch (US2). Leave fields out for the defaults: a deadline 30 days on, the standard attestation text in
/// every language of the version, reminders 7 and 2 days before, 14 days for late joiners, the policy's own audience.
/// </summary>
public sealed record CampaignRequest(
    DateOnly? Deadline,
    IReadOnlyDictionary<string, string>? AttestationText,
    IReadOnlyList<int>? ReminderDays,
    int? LateJoinerDays,
    IReadOnlyList<string>? ExcludedMembershipTypes,
    ContentAudience? Audience);

public sealed record CampaignCounts(int Members, int Attested, int Open, int Overdue, int LeftAudience);

public sealed record CampaignView(
    Guid Id,
    Guid PolicyId,
    int Version,
    ContentAudience Audience,
    DateTimeOffset Deadline,
    IReadOnlyDictionary<string, string> AttestationText,
    IReadOnlyList<int> ReminderDays,
    int LateJoinerDays,
    IReadOnlyList<string> ExcludedMembershipTypes,
    string Status,
    PolicyPerson LaunchedBy,
    DateTimeOffset LaunchedAt,
    DateTimeOffset? ClosedAt,
    CampaignCounts Counts);

public sealed record AttestRequest(Guid? CampaignId, string? Locale, string? Channel);

public sealed record AttestationView(Guid Id, Guid PolicyId, int Version, Guid? CampaignId, string Locale, string AttestationText, string TextHash, string Channel,
    DateTimeOffset AttestedAt);

/// <summary>An attestation the person still owes: the campaign, the version, the due date and the text to confirm.</summary>
public sealed record AttestationDue(Guid CampaignId, int Version, DateTimeOffset DueAt, bool Overdue, IReadOnlyDictionary<string, string> Text);

/// <summary>The current user's attestation state for one policy, for the policy page.</summary>
public sealed record MyAttestation(AttestationDue? Due, int? LastVersion, DateTimeOffset? LastAt);

/// <summary>One line of My policies: action needed when <see cref="Due"/> is set, accepted when the last attestation is for the version in force.</summary>
public sealed record MyPolicyItem(PolicySummary Policy, int? CurrentVersion, DateTimeOffset? EffectiveFrom, AttestationDue? Due, int? AttestedVersion, DateTimeOffset? AttestedAt);
