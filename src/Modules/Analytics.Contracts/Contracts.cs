using TeamBeats.Primitives;

namespace TeamBeats.Modules.Analytics.Contracts;

/// <summary>The kind of value an analytics field holds (spec 021 FR-003). Free text is never a field type, so no content or personal data can be collected.</summary>
public enum AnalyticsFieldType
{
    /// <summary>A UUID, for example the item that was viewed.</summary>
    Id,

    /// <summary>A short identifier: letters, digits, <c>_ . : -</c>, at most 64 characters.</summary>
    Key,

    /// <summary>A component reference with its version, as the renderer names it (<c>core.news_list@2</c>).</summary>
    ComponentRef,

    /// <summary>A whole number from 0 to 1,000,000,000.</summary>
    Count,

    /// <summary>A finite number from 0 to 1,000,000,000.</summary>
    Number,
}

public sealed record AnalyticsField(AnalyticsFieldType Type, bool Required = false, IReadOnlySet<string>? Allowed = null);

/// <summary>
/// One declared analytics event (spec 021 FR-003, FR-017). <paramref name="Essential"/> events are operational telemetry
/// that continue when the tenant turns nonessential analytics off and never carry a user key (FR-018);
/// the rest carry the pseudonymous key of the person, their org unit and location at event time, except
/// <paramref name="Unkeyed"/> events: counts of anonymous actions (spec 023 FR-024), which follow the tenant's choice like
/// other nonessential events but never carry a user, key, unit, location or locale, and keep only the day they happened.
/// </summary>
public sealed record AnalyticsSchema(string Name, bool Essential, IReadOnlyDictionary<string, AnalyticsField> Fields, bool Unkeyed = false)
{
    public bool UserKeyed => !Essential && !Unkeyed;
}

/// <summary>The declared events. An event or field not declared here is dropped and counted, never stored.</summary>
public static class AnalyticsEvents
{
    public const string ContentViewed = "content.viewed";
    public const string PageViewed = "page.viewed";
    public const string SearchQueried = "search.queried";
    public const string PersonalizationChanged = "personalization.changed";
    public const string TaskOutcome = "task.outcome";
    public const string UiError = "ui.error";
    public const string PageVitals = "page.vitals";
    public const string EngagementAction = "engagement.action";
    public const string EngagementAnonymousAction = "engagement.anonymous_action";
    public const string LearningProgress = "learning.progress";
    public const string BookingActivity = "booking.activity";

    /// <summary>Spec 016 FR-015: a request missed its service level target, counted per service without the person.</summary>
    public const string RequestServiceLevelBreached = "request.sla_breached";
    public const string MobileInstall = "mobile.install";
    public const string MobilePushOptIn = "mobile.push_optin";
    public const string ProcessInstanceEnded = "process.instance_ended";

    /// <summary>The sources whose items are viewed: content items, policies and documents.</summary>
    public static readonly IReadOnlySet<string> ItemSources = new HashSet<string>(StringComparer.Ordinal) { "content", "policies", "documents" };

    public static readonly IReadOnlySet<string> TaskOutcomes = new HashSet<string>(StringComparer.Ordinal) { "start", "complete", "abandon" };

    public static readonly IReadOnlySet<string> PersonalizationActions = new HashSet<string>(StringComparer.Ordinal) { "customize", "reset" };

    /// <summary>What a person did in communities and engagement (spec 023 FR-024, SC-001).</summary>
    public static readonly IReadOnlySet<string> EngagementActions = new HashSet<string>(StringComparer.Ordinal)
    {
        "post", "comment", "react", "vote", "recognize", "respond", "join",
    };

    /// <summary>Anonymous actions, counted with no identity at all (spec 023 FR-005, FR-024).</summary>
    public static readonly IReadOnlySet<string> EngagementAnonymousActions = new HashSet<string>(StringComparer.Ordinal)
    {
        "speak_up_post", "survey_response", "poll_vote",
    };

    /// <summary>What a learner did in a course (spec 028 section 13).</summary>
    public static readonly IReadOnlySet<string> LearningStatuses = new HashSet<string>(StringComparer.Ordinal)
    {
        "started", "lesson_completed", "quiz_passed", "quiz_failed", "course_completed",
    };

    /// <summary>What happened to a booking (spec 031 section 13), counted by resource type and site with no names.</summary>
    public static readonly IReadOnlySet<string> BookingActions = new HashSet<string>(StringComparer.Ordinal)
    {
        "booked", "requested", "approved", "rejected", "expired", "checked_in", "released", "cancelled",
    };

    /// <summary>Platform families of the installed app and push (spec 030 section 13); never a device identifier.</summary>
    public static readonly IReadOnlySet<string> MobilePlatforms = new HashSet<string>(StringComparer.Ordinal)
    {
        "android", "ios", "windows", "macos", "linux", "other",
    };

    /// <summary>How a process model instance ended (spec 024 FR-014), counted per process and version with its duration.</summary>
    public static readonly IReadOnlySet<string> ProcessEndStates = new HashSet<string>(StringComparer.Ordinal) { "completed", "cancelled" };

    public static readonly IReadOnlySet<string> BookingKinds = new HashSet<string>(StringComparer.Ordinal) { "room", "desk", "parking", "equipment" };

    /// <summary>Events only modules emit; the web collection endpoint drops them, so nobody can add to these counts by hand.</summary>
    public static readonly IReadOnlySet<string> ServerOnly = new HashSet<string>(StringComparer.Ordinal)
    {
        EngagementAction, EngagementAnonymousAction, LearningProgress, BookingActivity, MobilePushOptIn, ProcessInstanceEnded, RequestServiceLevelBreached,
    };

    public static readonly IReadOnlySet<string> DeviceClasses = new HashSet<string>(StringComparer.Ordinal) { "mobile", "tablet", "desktop" };

    public static readonly IReadOnlySet<string> ErrorKinds = new HashSet<string>(StringComparer.Ordinal) { "render", "boundary", "network", "chunk" };

    public static readonly IReadOnlyDictionary<string, AnalyticsSchema> Schemas = new Dictionary<string, AnalyticsSchema>(StringComparer.Ordinal)
    {
        [ContentViewed] = new(ContentViewed, false, new Dictionary<string, AnalyticsField>
        {
            ["source"] = new(AnalyticsFieldType.Key, true, ItemSources),
            ["itemId"] = new(AnalyticsFieldType.Id, true),
            // Spec 021 R2 FR-006: the site and content type filters of the workspace, when the page that reports the view knows them.
            ["contentType"] = new(AnalyticsFieldType.Key),
            ["siteId"] = new(AnalyticsFieldType.Id),
        }),
        [PageViewed] = new(PageViewed, false, new Dictionary<string, AnalyticsField>
        {
            ["area"] = new(AnalyticsFieldType.Key, true),
            ["pageId"] = new(AnalyticsFieldType.Id),
            ["template"] = new(AnalyticsFieldType.Key),
            ["templateVersion"] = new(AnalyticsFieldType.Count),
            ["siteId"] = new(AnalyticsFieldType.Id),
        }),
        [SearchQueried] = new(SearchQueried, false, new Dictionary<string, AnalyticsField>
        {
            ["results"] = new(AnalyticsFieldType.Count, true),
            ["kind"] = new(AnalyticsFieldType.Key),
        }),
        [PersonalizationChanged] = new(PersonalizationChanged, false, new Dictionary<string, AnalyticsField>
        {
            ["action"] = new(AnalyticsFieldType.Key, true, PersonalizationActions),
        }),
        [TaskOutcome] = new(TaskOutcome, false, new Dictionary<string, AnalyticsField>
        {
            ["flow"] = new(AnalyticsFieldType.Key, true),
            ["step"] = new(AnalyticsFieldType.Key),
            ["outcome"] = new(AnalyticsFieldType.Key, true, TaskOutcomes),
        }),
        [EngagementAction] = new(EngagementAction, false, new Dictionary<string, AnalyticsField>
        {
            ["action"] = new(AnalyticsFieldType.Key, true, EngagementActions),
            ["communityId"] = new(AnalyticsFieldType.Id),
        }),
        [EngagementAnonymousAction] = new(EngagementAnonymousAction, false, new Dictionary<string, AnalyticsField>
        {
            ["action"] = new(AnalyticsFieldType.Key, true, EngagementAnonymousActions),
        }, Unkeyed: true),
        [LearningProgress] = new(LearningProgress, false, new Dictionary<string, AnalyticsField>
        {
            ["courseId"] = new(AnalyticsFieldType.Id, true),
            ["lesson"] = new(AnalyticsFieldType.Key),
            ["status"] = new(AnalyticsFieldType.Key, true, LearningStatuses),
        }),
        [BookingActivity] = new(BookingActivity, false, new Dictionary<string, AnalyticsField>
        {
            ["action"] = new(AnalyticsFieldType.Key, true, BookingActions),
            ["type"] = new(AnalyticsFieldType.Key, true, BookingKinds),
            ["siteId"] = new(AnalyticsFieldType.Id, true),
        }, Unkeyed: true),
        [MobileInstall] = new(MobileInstall, false, new Dictionary<string, AnalyticsField>
        {
            ["platform"] = new(AnalyticsFieldType.Key, true, MobilePlatforms),
        }, Unkeyed: true),
        [MobilePushOptIn] = new(MobilePushOptIn, false, new Dictionary<string, AnalyticsField>
        {
            ["platform"] = new(AnalyticsFieldType.Key, true, MobilePlatforms),
        }, Unkeyed: true),
        [ProcessInstanceEnded] = new(ProcessInstanceEnded, false, new Dictionary<string, AnalyticsField>
        {
            ["process"] = new(AnalyticsFieldType.Key, true),
            ["version"] = new(AnalyticsFieldType.Count, true),
            ["state"] = new(AnalyticsFieldType.Key, true, ProcessEndStates),
            ["seconds"] = new(AnalyticsFieldType.Number, true),
        }, Unkeyed: true),
        [RequestServiceLevelBreached] = new(RequestServiceLevelBreached, false, new Dictionary<string, AnalyticsField>
        {
            ["serviceId"] = new(AnalyticsFieldType.Id, true),
        }, Unkeyed: true),
        // Spec 021 FR-020: Core Web Vitals of a spec 032 page, essential performance telemetry with no person in it.
        [PageVitals] = new(PageVitals, true, new Dictionary<string, AnalyticsField>
        {
            ["pageId"] = new(AnalyticsFieldType.Id, true),
            ["release"] = new(AnalyticsFieldType.Count),
            ["deviceClass"] = new(AnalyticsFieldType.Key, true, DeviceClasses),
            ["theme"] = new(AnalyticsFieldType.Key),
            ["lcp"] = new(AnalyticsFieldType.Number),
            ["inp"] = new(AnalyticsFieldType.Number),
            ["cls"] = new(AnalyticsFieldType.Number),
        }),
        [UiError] = new(UiError, true, new Dictionary<string, AnalyticsField>
        {
            ["kind"] = new(AnalyticsFieldType.Key, true, ErrorKinds),
            ["release"] = new(AnalyticsFieldType.Key, true),
            ["component"] = new(AnalyticsFieldType.ComponentRef),
            ["area"] = new(AnalyticsFieldType.Key),
            ["correlationId"] = new(AnalyticsFieldType.Key),
        }),
    };
}

/// <summary>Where an event happened (spec 021 FR-003). Server events are emitted by modules.</summary>
public static class AnalyticsChannels
{
    public const string Web = "web";
    public const string Mobile = "mobile";
    public const string Teams = "teams";
    public const string Server = "server";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal) { Web, Mobile, Teams, Server };
}

/// <summary>
/// Emits an analytics event for the signed-in person (spec 021 FR-003). The event is queued through the outbox and written
/// later, so it never slows the action that caused it (section 15). Fields follow the declared schema; anything else is dropped.
/// </summary>
public interface IAnalyticsEvents
{
    Task EmitAsync(string name, IReadOnlyDictionary<string, object?> fields, CancellationToken ct = default);
}

/// <summary>
/// An event queued by a module. <paramref name="Fields"/> are already strings in invariant form; the writer checks them
/// against the schema again, so a malformed message is dropped like any other.
/// </summary>
public sealed record AnalyticsEventEmitted(string Name, Guid? UserId, string Channel, string? Locale, DateTimeOffset OccurredAt,
    IReadOnlyDictionary<string, string> Fields) : IIntegrationEvent;

/// <summary>
/// What an owning module tells Analytics about one of its items (spec 021 US1, plan "Item reach and acknowledgement"):
/// its titles, when it went live, the people it targets now, whether it asks for acknowledgement and who has given it,
/// and its reaction and comment counts. Analytics never reads the owner's tables.
/// </summary>
public sealed record AnalyticsItem(
    Guid ItemId,
    IReadOnlyDictionary<string, string> Titles,
    DateTimeOffset? LiveFrom,
    IReadOnlySet<Guid> Audience,
    bool AcknowledgementRequired,
    IReadOnlySet<Guid> Acknowledged,
    int Reactions,
    int Comments);

/// <summary>
/// An owning module whose items have analytics (content items of spec 009, policies of spec 025). The owner decides who
/// may see an item's analytics: an item the person may not read is not found, and one they read without rights on it
/// is denied. Reminders go out through the owner, which sends them through notifications (spec 015).
/// </summary>
public interface IAnalyticsItemSource
{
    /// <summary>The source name, as in <c>content.viewed</c> events (<c>content</c>, <c>policies</c>).</summary>
    string Source { get; }

    Task<AnalyticsItem> DescribeAsync(Guid itemId, CancellationToken ct = default);

    /// <summary>Reminds the given people of the item, after checking again that the person asking may; returns how many were sent.</summary>
    Task<int> RemindAsync(Guid itemId, IReadOnlyCollection<Guid> people, CancellationToken ct = default);
}

/// <summary>A campaign the person looks after, as its owner lists it (spec 021 US2, spec 009 US8).</summary>
public sealed record AnalyticsCampaign(Guid Id, string Name, Guid SiteId, string Status, DateTimeOffset CreatedAt, int Steps, DateTimeOffset? FirstSentAt);

/// <summary>
/// One step of a campaign with raw counts: the people it went to, how many notifications went out, and how many of those
/// people opened the item, came through the campaign link and completed its call to action (confirmed it when it asks
/// for confirmation, otherwise opened it). <see cref="Link"/> is the notification link of the step.
/// </summary>
public sealed record AnalyticsCampaignStep(Guid StepId, int Position, Guid ItemId, IReadOnlyDictionary<string, string> Titles, string Status, DateTimeOffset At,
    DateTimeOffset? SentAt, string Link, bool AcknowledgementRequired, int Audience, int Delivered, int Viewed, int Clicked, int Completed);

/// <summary>Raw counts of the people a campaign reached in one unit or location (<see cref="Dimension"/> is <c>unit</c> or <c>location</c>).</summary>
public sealed record AnalyticsCampaignGroup(string Dimension, string Key, int Audience, int Viewed, int Completed);

/// <summary>A campaign's funnel: its steps and, per distinct person over all steps, reached, opened, clicked and completed.</summary>
public sealed record AnalyticsCampaignFunnel(AnalyticsCampaign Campaign, IReadOnlyList<AnalyticsCampaignStep> Steps, int People, int Viewed, int Clicked, int Completed,
    IReadOnlyList<AnalyticsCampaignGroup> Groups);

/// <summary>
/// The owner of campaigns (Content, spec 009 US8). It decides who may see a campaign's analytics, the people who may run
/// it, and answers a campaign they may not see as not found. Counts are raw: Analytics applies the minimum group size.
/// </summary>
public interface IAnalyticsCampaignSource
{
    Task<IReadOnlyList<AnalyticsCampaign>> ListAsync(CancellationToken ct = default);

    Task<AnalyticsCampaignFunnel> FunnelAsync(Guid campaignId, CancellationToken ct = default);
}

/// <summary>A service the person owns, for service analytics (spec 021 US4, spec 016). <see cref="ProcessKey"/> is its route in the process engine.</summary>
public sealed record AnalyticsService(Guid Id, IReadOnlyDictionary<string, string> Names, string ProcessKey, Guid? OwnerOrgUnitId);

/// <summary>
/// One decision on a request step: its outcome (<c>approved</c>, <c>rejected</c>, <c>returned</c>, <c>done</c>,
/// <c>skipped</c>, <c>failed</c>), how long the step waited since the previous event of the request, and whether
/// automation decided it rather than a person.
/// </summary>
public sealed record AnalyticsStepFact(string Step, string Outcome, double Seconds, bool Automated);

/// <summary>
/// One request of a service with no person in it: when it was filed and closed, its status, the requester's unit, whether
/// it breached its service level, its step decisions, and the values of the service's reportable choice fields.
/// </summary>
public sealed record AnalyticsRequestFact(DateTimeOffset SubmittedAt, DateTimeOffset? ClosedAt, string Status, Guid? OrgUnitId, bool Breached,
    IReadOnlyList<AnalyticsStepFact> Steps, IReadOnlyDictionary<string, string> Reasons);

public sealed record AnalyticsReasonOption(string Value, IReadOnlyDictionary<string, string> Labels);

/// <summary>A structured reason: a single choice field the service owner marked reportable (spec 016 FR-012).</summary>
public sealed record AnalyticsReasonField(string Key, IReadOnlyDictionary<string, string> Labels, IReadOnlyList<AnalyticsReasonOption> Options);

public sealed record AnalyticsServiceFacts(AnalyticsService Service, IReadOnlyList<AnalyticsRequestFact> Requests, IReadOnlyList<AnalyticsReasonField> ReasonFields);

/// <summary>
/// The owner of services and requests (Services, spec 016). It decides who may see a service's analytics, its owners
/// with <c>service.manage</c> at the owning unit, and answers a service they may not see as not found. The facts are raw:
/// Analytics applies the minimum group size.
/// </summary>
public interface IAnalyticsServiceSource
{
    Task<IReadOnlyList<AnalyticsService>> ListAsync(CancellationToken ct = default);

    /// <summary>The requests filed from <paramref name="from"/> up to, not including, <paramref name="to"/>.</summary>
    Task<AnalyticsServiceFacts> FactsAsync(Guid serviceId, DateTimeOffset from, DateTimeOffset to, CancellationToken ct = default);
}

/// <summary>
/// One member of an attestation campaign with no person in it: the primary unit, the location key, the status
/// (<c>open</c>, <c>attested</c>, <c>overdue</c>) and when they attested. People who left the audience are not given.
/// </summary>
public sealed record AnalyticsComplianceMember(Guid? OrgUnitId, string? Location, string Status, DateTimeOffset? AttestedAt);

/// <summary>An open attestation campaign for the version in force of a policy (spec 025), with the viewer's members.</summary>
public sealed record AnalyticsComplianceCampaign(Guid CampaignId, Guid PolicyId, int Version, IReadOnlyDictionary<string, string> Names,
    DateTimeOffset LaunchedAt, DateTimeOffset Deadline, IReadOnlyList<AnalyticsComplianceMember> Members);

/// <summary>
/// The owner of policies and attestation campaigns (Policies, spec 025). It decides whose attestations a viewer may count:
/// the people placed in the units where they hold <c>policy.manage</c>, everyone at tenant scope; anyone without it is
/// denied. Campaigns with none of the viewer's people are left out. The facts are raw: Analytics applies the minimum group size.
/// </summary>
public interface IAnalyticsComplianceSource
{
    Task<IReadOnlyList<AnalyticsComplianceCampaign>> OpenCampaignsAsync(CancellationToken ct = default);
}

/// <summary>
/// One figure of a module for a set of people (spec 021 US6): <see cref="Value"/> out of <see cref="Of"/> for a share,
/// or a count alone when <see cref="Of"/> is null. Keys are listed in <see cref="AnalyticsScopeFigures"/>.
/// </summary>
public sealed record AnalyticsScopeFigure(string Key, int Value, int? Of);

public static class AnalyticsScopeFigures
{
    /// <summary>People attested out of the people in open campaigns for the version in force (Policies).</summary>
    public const string Compliance = "compliance";

    /// <summary>Requests the people filed that are still open (Services); a count.</summary>
    public const string OpenRequests = "open_requests";

    /// <summary>Requests filed in the period that breached their service level, out of those filed (Services).</summary>
    public const string Breaches = "breaches";

    /// <summary>Working days with an exception (late, early leave, missing punch, absent) out of working days (HR attendance).</summary>
    public const string AttendanceExceptions = "attendance_exceptions";
}

/// <summary>
/// A module's figures for the location and unit dashboards (spec 021 US6, FR-010). Analytics has already decided that the
/// viewer may see the scope these people make up, through <c>analytics.view</c>; the module answers counts only, never
/// a person, and nothing when its capability is off. Analytics applies the minimum group size.
/// </summary>
public interface IAnalyticsScopeSource
{
    Task<IReadOnlyList<AnalyticsScopeFigure>> FiguresAsync(IReadOnlyCollection<Guid> people, DateTimeOffset from, DateTimeOffset to, CancellationToken ct = default);
}

/// <summary>
/// The person's pseudonymous key in this tenant (spec 021 FR-003), for a module that keeps its own pseudonymous records,
/// such as the search query log (spec 012 FR-013). The same key as analytics events carry.
/// </summary>
public interface IAnalyticsPseudonyms
{
    Task<string> KeyAsync(Guid userId, CancellationToken ct = default);
}

/// <summary>The kinds of free text sentiment is worked out from (spec 021 US8).</summary>
public static class AnalyticsTextSources
{
    public const string ContentComment = "content_comment";
    public const string CommunityComment = "community_comment";
    public const string SurveyAnswer = "survey_answer";
}

/// <summary>Where a source left off: the time and id of the last text read. Texts come in that order.</summary>
public sealed record AnalyticsTextCursor(DateTimeOffset At, Guid Id);

/// <summary>
/// One free text for sentiment (spec 021 US8, FR-013). <see cref="AuthorId"/> is set only when the text is not anonymous,
/// so Analytics can count it in the author's unit and location and then drops it; an anonymous survey answer carries
/// only the group unit it already counts in and the day its survey closed. <see cref="Classification"/> is the source's
/// data classification (public, internal, confidential, restricted) for the AI Gateway. The text is never stored.
/// </summary>
public sealed record AnalyticsText(AnalyticsTextCursor Cursor, string Text, DateOnly Day, Guid? AuthorId, Guid? GroupUnitId, string Topic, string Classification);

/// <summary>
/// A module's free text for sentiment, oldest first after a cursor. The module decides what counts: visible comments,
/// and survey text answers once the survey has closed. Only asked when the tenant has turned sentiment on.
/// </summary>
public interface IAnalyticsTextSource
{
    string Source { get; }

    Task<IReadOnlyList<AnalyticsText>> AfterAsync(AnalyticsTextCursor? after, int limit, CancellationToken ct = default);
}
