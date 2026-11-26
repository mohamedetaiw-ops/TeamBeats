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

    /// <summary>Published shifts and roster days off (spec 037 R3, FR-034).</summary>
    public const string MyShifts = "my_shifts";

    public static readonly string[] All = [Meeting, Company, Department, Important, Holiday, MyLeave, TeamLeave, MyShifts];

    public static string ForKind(string kind) => kind switch
    {
        EventKinds.Department => Department,
        EventKinds.Important => Important,
        _ => Company,
    };
}

/// <summary>How long before an occurrence its reminder goes (FR-032).</summary>
public static class EventReminders
{
    public const string None = "none";
    public static readonly string[] All = [None, "15m", "1h", "1d"];

    public static TimeSpan? Offset(string reminder) => reminder switch
    {
        "15m" => TimeSpan.FromMinutes(15),
        "1h" => TimeSpan.FromHours(1),
        "1d" => TimeSpan.FromDays(1),
        _ => null,
    };
}

public static class RegistrationStates
{
    public const string Registered = "registered";
    public const string Waiting = "waiting";
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
    public const string Shifts = "shifts";
}

/// <summary>What the employee may do with an item; the page offers only these (FR-008).</summary>
public static class ItemActions
{
    public const string Join = "join";
    public const string OpenSource = "open_source";
    public const string Rsvp = "rsvp";

    /// <summary>Register, join the waiting list or cancel (FR-028); also download the calendar file of the occurrence.</summary>
    public const string Register = "register";
    public const string CalendarFile = "calendar_file";
    public const string OpenLeave = "open_leave";
    public const string RequestLeave = "request_leave";
    public const string ViewTeam = "view_team";

    /// <summary>Accept, tentative or decline an Outlook invitation from TeamBeats (spec 018 R3, FR-015).</summary>
    public const string Answer = "answer";
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
    RegistrationView? Registration = null,
    IReadOnlyList<AttachmentView>? Attachments = null,
    string? OccurrenceKey = null,
    bool Recurring = false,
    Guid? LeaveRequestId = null,
    string? TypeKey = null,
    string? Invitation = null,
    bool Provisional = false,
    DateOnly? ObservedFor = null)
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

public sealed record HolidayAlert(DateOnly Date, string Title, bool HalfDay, bool Provisional = false, DateOnly? ObservedFor = null);

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

public sealed record HolidayView(DateOnly Date, DateOnly EndDate, string Title, bool HalfDay, bool Provisional = false, DateOnly? ObservedFor = null);

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
    bool Rsvp,
    bool Registration = false,
    int? Capacity = null,
    bool Waitlist = false,
    DateTimeOffset? RegistrationClosesAt = null,
    string? Reminder = null);

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
    DateTimeOffset UpdatedAt,
    bool Registration = false,
    int? Capacity = null,
    bool Waitlist = false,
    DateTimeOffset? RegistrationClosesAt = null,
    string Reminder = "none",
    IReadOnlyList<AttachmentView>? Attachments = null)
{
    public string ETag => $"\"v{Version}\"";
}

/// <summary>
/// The viewer's place at an occurrence with registration (spec 037 FR-028 to FR-030): <see cref="State"/> is registered,
/// waiting or null; <see cref="Position"/> is the place on the waiting list. Taken and waiting counts only for managers.
/// </summary>
public sealed record RegistrationView(string? State, int? Position, int SeatsLeft, bool Open, bool Waitlist, int? Taken = null, int? Waiting = null);

public sealed record RegistrationRequest(string? OccurrenceKey);

public sealed record RegistrationCounts(string OccurrenceKey, int Capacity, int Taken, int Waiting);

public sealed record RegistrationPerson(string OccurrenceKey, PersonRef Person, string State, DateTimeOffset JoinedAt);

public sealed record RegistrationsView(IReadOnlyList<RegistrationCounts> Counts, IReadOnlyList<RegistrationPerson> People);

/// <summary>A file attached to an event (FR-033). It is read through the calendar API with the audience check.</summary>
public sealed record AttachmentView(Guid Id, string Name, string ContentType, long Size, DateTimeOffset CreatedAt);

public sealed record ResponseRequest(string? OccurrenceKey, string? Answer);

public sealed record ResponseCounts(string OccurrenceKey, int Going, int Maybe, int Declined);

public sealed record ResponsePerson(string OccurrenceKey, PersonRef Person, string Answer, DateTimeOffset AnsweredAt);

public sealed record ResponsesView(IReadOnlyList<ResponseCounts> Counts, IReadOnlyList<ResponsePerson>? People);
