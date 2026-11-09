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
/// the rest carry the pseudonymous key of the person, their org unit and location at event time.
/// </summary>
public sealed record AnalyticsSchema(string Name, bool Essential, IReadOnlyDictionary<string, AnalyticsField> Fields)
{
    public bool UserKeyed => !Essential;
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

    /// <summary>The sources whose items are viewed: content items, policies and documents.</summary>
    public static readonly IReadOnlySet<string> ItemSources = new HashSet<string>(StringComparer.Ordinal) { "content", "policies", "documents" };

    public static readonly IReadOnlySet<string> TaskOutcomes = new HashSet<string>(StringComparer.Ordinal) { "start", "complete", "abandon" };

    public static readonly IReadOnlySet<string> PersonalizationActions = new HashSet<string>(StringComparer.Ordinal) { "customize", "reset" };

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
