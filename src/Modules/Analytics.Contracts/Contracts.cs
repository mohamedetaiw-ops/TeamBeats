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

    public static readonly IReadOnlySet<string> ErrorKinds = new HashSet<string>(StringComparer.Ordinal) { "render", "boundary", "network", "chunk" };

    public static readonly IReadOnlyDictionary<string, AnalyticsSchema> Schemas = new Dictionary<string, AnalyticsSchema>(StringComparer.Ordinal)
    {
        [ContentViewed] = new(ContentViewed, false, new Dictionary<string, AnalyticsField>
        {
            ["source"] = new(AnalyticsFieldType.Key, true, ItemSources),
            ["itemId"] = new(AnalyticsFieldType.Id, true),
        }),
        [PageViewed] = new(PageViewed, false, new Dictionary<string, AnalyticsField>
        {
            ["area"] = new(AnalyticsFieldType.Key, true),
            ["pageId"] = new(AnalyticsFieldType.Id),
            ["template"] = new(AnalyticsFieldType.Key),
            ["templateVersion"] = new(AnalyticsFieldType.Count),
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
