using System.Text.Json.Serialization;

namespace TeamBeats.Modules.Calendar;

/// <summary>The layers of the unified calendar (spec 037 section 2). Turning one off never changes the data under it.</summary>
public static class CalendarLayers
{
    public const string Meeting = "meeting";
    public const string Company = "company";
    public const string Department = "department";
    public const string Important = "important";
    public const string Holiday = "holiday";
    public const string MyLeave = "my_leave";
    public const string TeamLeave = "team_leave";

    public static readonly string[] All = [Meeting, Company, Department, Important, Holiday, MyLeave, TeamLeave];

    public static string ForKind(string kind) => kind switch
    {
        EventKinds.Department => Department,
        EventKinds.Important => Important,
        _ => Company,
    };
}

public static class EventKinds
{
    public const string Company = "company";
    public const string Department = "department";
    public const string Important = "important";

    public static readonly string[] All = [Company, Department, Important];
}

/// <summary>Where a calendar item comes from.</summary>
public static class ItemSources
{
    public const string Outlook = "outlook";
    public const string Google = "google";
    public const string Events = "events";
    public const string Holidays = "holidays";
    public const string Leave = "leave";
}

/// <summary>What the employee may do with an item; the page offers only these (FR-008).</summary>
public static class ItemActions
{
    public const string Join = "join";
    public const string OpenSource = "open_source";
    public const string Rsvp = "rsvp";
    public const string OpenLeave = "open_leave";
    public const string RequestLeave = "request_leave";
    public const string ViewTeam = "view_team";
}

public static class ItemStatuses
{
    public const string Confirmed = "confirmed";
    public const string Tentative = "tentative";
    public const string Pending = "pending";
    public const string Cancelled = "cancelled";
}

public static class CalendarViews
{
    public static readonly string[] All = ["agenda", "week", "month", "team"];
}

/// <summary>An RSVP as the viewer sees it: their own answer, and the counts only for those who manage the event.</summary>
public sealed record RsvpView(string? Answer, bool Open, int? Going = null, int? Maybe = null, int? Declined = null);

/// <summary>A person reference: a name and ID, never more.</summary>
public sealed record PersonRef(Guid UserId, string DisplayName);

/// <summary>
/// The canonical calendar item (spec 037 FR-001). Timed items carry <see cref="Start"/> and <see cref="End"/> as UTC
/// instants; all-day items carry <see cref="StartDate"/> and an exclusive <see cref="EndDate"/>. Fields the viewer may
/// not see are null, never blank text.
/// </summary>
public sealed record CalendarItemView(
    string Id,
    string Layer,
    string Source,
    string SourceId,
    string? Title,
    bool AllDay,
    DateTimeOffset? Start,
    DateTimeOffset? End,
    DateOnly? StartDate,
    DateOnly? EndDate,
    string TimeZone,
    string Status,
    string Visibility,
    IReadOnlyList<string> Actions,
    string? Location = null,
    string? OnlineUrl = null,
    string? WebLink = null,
    string? Description = null,
    bool Free = false,
    bool IsPrivate = false,
    bool HalfStart = false,
    bool HalfEnd = false,
    int? Count = null,
    PersonRef? Person = null,
    RsvpView? Rsvp = null,
    string? OccurrenceKey = null,
    bool Recurring = false,
    Guid? LeaveRequestId = null,
    string? TypeKey = null)
{
    /// <summary>The first local date the item touches, for ordering and grouping.</summary>
    [JsonIgnore]
    public DateOnly SortDate { get; init; }
}

/// <summary>The state of one source for this answer (FR-005, FR-023). <see cref="State"/> is ok, degraded or a setup state.</summary>
public sealed record SourceStatus(string Source, string State, DateTimeOffset? AsOf);

public sealed record CalendarEventsResponse(
    string TimeZone,
    DateOnly From,
    DateOnly To,
    IReadOnlyList<string> Layers,
    IReadOnlyList<SourceStatus> Sources,
    IReadOnlyList<CalendarItemView> Items,
    string? NextCursor);

public sealed record DaySummary(DateOnly Date, IReadOnlyDictionary<string, int> Layers, int TeamAway);

public sealed record HolidayAlert(DateOnly Date, string Title, bool HalfDay);

public sealed record CalendarAlerts(IReadOnlyList<HolidayAlert> Holidays, int TeamAwayToday);

public sealed record CalendarSummaryResponse(
    string TimeZone,
    DateOnly Today,
    DateOnly From,
    DateOnly To,
    IReadOnlyList<string> Available,
    IReadOnlyList<string> Enabled,
    string View,
    bool IsManager,
    IReadOnlyList<DaySummary> Days,
    CalendarAlerts Alerts,
    IReadOnlyList<SourceStatus> Sources);

public sealed record HolidayView(DateOnly Date, DateOnly EndDate, string Title, bool HalfDay);

public sealed record PreferencesView(IReadOnlyList<string> Available, IReadOnlyList<string> Layers, string View);

public sealed record PreferencesRequest(IReadOnlyList<string>? Layers, string? View);

public sealed record SettingsView(int Version, bool IndirectReports, int Threshold, IReadOnlyList<string> DefaultLayers, DateTimeOffset? UpdatedAt);

public sealed record SettingsRequest(bool? IndirectReports, int? Threshold, IReadOnlyList<string>? DefaultLayers);

public sealed record TeamDay(DateOnly Date, bool Working, int Total, decimal Away, decimal Available, int? Share, bool Breach);

public sealed record TeamRange(DateOnly Start, DateOnly End, string Status, bool HalfStart, bool HalfEnd, string? TypeKey, string? TypeName, Guid? RequestId);

public sealed record TeamMember(Guid UserId, string DisplayName, IReadOnlyList<TeamRange> Leave);

public sealed record UpcomingLeave(Guid UserId, string DisplayName, DateOnly Start, DateOnly End, string Status, string? TypeName);

public sealed record TeamToday(int Available, int Total, int OnLeave, int StartingSoon);

public sealed record TeamAvailabilityResponse(
    string TimeZone,
    DateOnly From,
    DateOnly To,
    DateOnly Today,
    int TeamSize,
    int Threshold,
    bool IndirectReports,
    TeamToday TodayCounts,
    IReadOnlyList<TeamDay> Days,
    IReadOnlyList<TeamMember> People,
    IReadOnlyList<UpcomingLeave> Upcoming,
    int More);

/// <summary>A recurrence rule of a TeamBeats series (FR-017): daily, weekly on chosen ISO weekdays, or monthly on the start's day.</summary>
public sealed record RecurrenceRule(string Frequency, int Interval = 1, IReadOnlyList<int>? Weekdays = null, DateOnly? Until = null, int? Count = null);

public sealed record EventRequest(
    IReadOnlyDictionary<string, string>? Titles,
    IReadOnlyDictionary<string, string>? Descriptions,
    string? Kind,
    Guid? OrgUnitId,
    bool AllDay,
    DateTimeOffset? Start,
    DateTimeOffset? End,
    DateOnly? StartDate,
    DateOnly? EndDate,
    string? TimeZone,
    string? Location,
    string? OnlineUrl,
    TeamBeats.Modules.Content.Contracts.ContentAudience? Audience,
    RecurrenceRule? Recurrence,
    bool Rsvp);

public sealed record EventView(
    Guid Id,
    Guid? OrgUnitId,
    string Kind,
    IReadOnlyDictionary<string, string> Titles,
    IReadOnlyDictionary<string, string> Descriptions,
    bool AllDay,
    DateTimeOffset? Start,
    DateTimeOffset? End,
    DateOnly? StartDate,
    DateOnly? EndDate,
    string TimeZone,
    string? Location,
    string? OnlineUrl,
    TeamBeats.Modules.Content.Contracts.ContentAudience Audience,
    RecurrenceRule? Recurrence,
    bool Rsvp,
    string Status,
    DateTimeOffset? CancelledAt,
    int Version,
    DateTimeOffset UpdatedAt)
{
    public string ETag => $"\"v{Version}\"";
}

public sealed record ResponseRequest(string? OccurrenceKey, string? Answer);

public sealed record ResponseCounts(string OccurrenceKey, int Going, int Maybe, int Declined);

public sealed record ResponsePerson(string OccurrenceKey, PersonRef Person, string Answer, DateTimeOffset AnsweredAt);

public sealed record ResponsesView(IReadOnlyList<ResponseCounts> Counts, IReadOnlyList<ResponsePerson>? People);
