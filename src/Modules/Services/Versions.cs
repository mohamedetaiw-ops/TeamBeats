using TeamBeats.Persistence;

namespace TeamBeats.Modules.Services;

/// <summary>
/// Approver rule kinds of R1: named users, holders of a permission at the requester's unit ("role at unit"), or the
/// line manager of the person the request is for. A line manager rule names the permission its step falls back to
/// when there is no manager (default <c>workflow.approve</c> at the requester's unit).
/// </summary>
public static class ApproverKinds
{
    public const string Users = "users";
    public const string PermissionAtRequesterUnit = "permission_at_unit";
    public const string LineManager = "line_manager";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal) { Users, PermissionAtRequesterUnit, LineManager };
}

public sealed record ApproverRule(string Kind, IReadOnlyList<Guid>? Users = null, string? Permission = null);

public sealed record ApprovalStep(IReadOnlyDictionary<string, string>? Names, ApproverRule Approvers, int? DueWorkingDays = null);

/// <summary>
/// A step that calls a connector action after the approval (spec 016 US8, FR-016; spec 017 T009). <paramref name="Inputs"/>
/// maps each action input to a request value: <c>form.{field}</c>, <c>request.number</c>, <c>requester.name</c> or
/// <c>requester.email</c>. Only the mapped values are sent. <paramref name="OnBusinessError"/> maps declared business
/// error codes to <c>continue</c> or <c>reject</c>; other failures pause the request at the step ("automation failed").
/// </summary>
public sealed record ActionStep(IReadOnlyDictionary<string, string>? Names, Guid ConnectionId, string Action, IReadOnlyDictionary<string, string>? Inputs,
    IReadOnlyDictionary<string, string>? OnBusinessError = null);

/// <summary>What starts a request besides a person's submit (spec 016 FR-014).</summary>
public static class TriggerKinds
{
    public const string Schedule = "schedule";
    public const string Event = "event";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal) { Schedule, Event };
}

/// <summary>The product events a trigger listens to (FR-014).</summary>
public static class TriggerEvents
{
    public const string UserProvisioned = "user_provisioned";
    public const string UserDeprovisioned = "user_deprovisioned";
    public const string MembershipChanged = "membership_changed";
    public const string RequestCompleted = "request_completed";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
    {
        UserProvisioned, UserDeprovisioned, MembershipChanged, RequestCompleted,
    };
}

public static class ScheduleFrequencies
{
    public const string Weekly = "weekly";
    public const string Monthly = "monthly";
    public const string Quarterly = "quarterly";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal) { Weekly, Monthly, Quarterly };
}

/// <summary>
/// A start trigger of a service (spec 016 FR-014, US8 scenario 2). A schedule starts one request each period on
/// <paramref name="Day"/> (weekly: 1 Monday to 7 Sunday; monthly and quarterly: day 1 to 28 of the month, the quarter's
/// first month) for <paramref name="RequesterId"/>, the service's system requester. An event starts one request for the
/// person the event is about, filed by <paramref name="RequesterId"/> when set (else the person files it): <c>membership_changed</c> may name the unit (<paramref name="OrgUnitId"/>), and
/// <c>request_completed</c> names the other service (<paramref name="ServiceId"/>) and may name the end status.
/// </summary>
public sealed record ServiceTrigger(string Kind, string? Event = null, Guid? ServiceId = null, string? Status = null, Guid? OrgUnitId = null,
    string? Frequency = null, int? Day = null, Guid? RequesterId = null);

/// <summary>
/// A service's route: either the R1 shape (none or one approval step, then the connector steps in order) or a step
/// graph built in the designer (spec 016 R2, US5 and US8), which runs on the process engine as a compiled model. It also
/// holds the start triggers (FR-014), whether requests continue after the requester leaves, and the service level target
/// (FR-015), so each version keeps its own.
/// </summary>
public sealed record ServiceRoute(ApprovalStep? Approval = null, IReadOnlyList<ActionStep>? Actions = null, RouteGraph? Graph = null,
    IReadOnlyList<ServiceTrigger>? Triggers = null, bool ContinueAfterExit = false, int? ServiceLevelDays = null)
{
    /// <summary>The longest service level target, in working days on the requester unit's calendar (FR-015).</summary>
    public const int MaxServiceLevelDays = 90;

    public static readonly ServiceRoute None = new();

    public const int MaxActions = 5;
    public const int MaxTriggers = 10;

    public bool HasSteps => Approval is not null || Actions is { Count: > 0 } || Graph is not null;
}

public sealed record VersionRow(
    int Version,
    string State,
    FormDefinition Form,
    ServiceRoute Route,
    Guid? FulfilmentQueueGroupId,
    bool AllowOnBehalf,
    int MaxAttachments,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? PublishedAt);

internal static class Versions
{
    private const string Columns = "version, state, form::text, route::text, fulfilment_queue_group_id, allow_on_behalf, max_attachments, updated_at, published_at";

    private static VersionRow Read(Npgsql.NpgsqlDataReader r) => new(
        r.GetInt32(0), r.GetString(1), Catalogue.Json<FormDefinition>(r.GetString(2)), Catalogue.Json<ServiceRoute>(r.GetString(3)),
        r.Get<Guid?>("fulfilment_queue_group_id"), r.GetBoolean(5), r.GetInt32(6), r.GetFieldValue<DateTimeOffset>(7), r.Get<DateTimeOffset?>("published_at"));

    public static Task<VersionRow?> GetAsync(IDbSession session, Guid serviceId, int version, CancellationToken ct) =>
        session.QuerySingleOrDefaultAsync($"select {Columns} from services.service_version where service_id = @serviceId and version = @version",
            new { serviceId, version }, Read, ct);

    public static Task<VersionRow?> LatestAsync(IDbSession session, Guid serviceId, bool forUpdate, CancellationToken ct) =>
        session.QuerySingleOrDefaultAsync(
            $"select {Columns} from services.service_version where service_id = @serviceId order by version desc limit 1" + (forUpdate ? " for update" : ""),
            new { serviceId }, Read, ct);
}
