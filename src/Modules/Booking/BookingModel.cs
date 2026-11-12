using System.Text.Json;
using Npgsql;
using TeamBeats.Entitlements.Contracts;
using TeamBeats.Persistence;

namespace TeamBeats.Modules.Booking;

public static class ResourceKinds
{
    public const string Room = "room";
    public const string Desk = "desk";
    public const string Parking = "parking";
    public const string Equipment = "equipment";

    public static readonly IReadOnlyList<string> All = [Room, Desk, Parking, Equipment];

    /// <summary>The capability that puts a kind of resource in the plan (spec 031 section 9).</summary>
    public static string Capability(string kind) => kind switch
    {
        Room => Capabilities.BookingRooms,
        Desk => Capabilities.BookingDesks,
        _ => Capabilities.BookingResources,
    };
}

public static class BookingStatuses
{
    public const string Tentative = "tentative";
    public const string Confirmed = "confirmed";
    public const string CheckedIn = "checked_in";
    public const string Released = "released";
    public const string Cancelled = "cancelled";
    public const string Rejected = "rejected";
    public const string Expired = "expired";

    /// <summary>Statuses that hold the resource; the exclusion constraint allows one of them at any moment.</summary>
    public static readonly string[] Live = [Tentative, Confirmed, CheckedIn];
}

// ---- Requests ----

public sealed record SiteRequest(Dictionary<string, string>? Names, string? LocationKey, Guid? OrgUnitId, string? TimeZone, TimeOnly? OpensAt, TimeOnly? ClosesAt, int? Version);

public sealed record PlaceRequest(Dictionary<string, string>? Names, int? Sort);

public sealed record ResourceRequest(
    Guid? SiteId,
    Guid? FloorId,
    Guid? ZoneId,
    string? Kind,
    Dictionary<string, string>? Names,
    int? Capacity,
    IReadOnlyList<string>? Attributes,
    Guid? FixedUserId,
    string? Mailbox,
    bool? CheckIn,
    bool? NeedsApproval,
    int? Version,
    IReadOnlyList<Guid>? ApproverIds = null);

public sealed record RuleRequest(
    IReadOnlyList<Guid>? UnitIds,
    int? AdvanceDays,
    int? MinMinutes,
    int? MaxMinutes,
    bool? WorkingHours,
    int? BufferMinutes,
    int? MaxActive,
    bool? Overnight,
    int? SeriesDays);

public sealed record SettingsRequest(int? SlotMinutes, int? CheckInBeforeMinutes, int? CheckInGraceMinutes, int? ApprovalHoldHours, int? NoShowLimit,
    int? NoShowWindowDays, bool? NoShowRestrict, int? NoShowRestrictDays, int? NoShowAdvanceDays, bool? ShowNames, int? Version);

/// <summary>A repeat of a booking (FR-006): daily or weekly, up to and including <paramref name="Until"/>.</summary>
public sealed record RepeatRequest(string? Pattern, DateOnly? Until);

/// <summary>
/// A booking request. Date and times are in the site's time zone (FR-017); an end at or before the start means the
/// next day, which only resources whose rules allow overnight bookings accept.
/// </summary>
public sealed record BookRequest(Guid? ResourceId, DateOnly? Date, TimeOnly? Start, TimeOnly? End, string? Title, RepeatRequest? Repeat);

public sealed record CancelRequest(string? Reason);

/// <summary>Check-in methods (FR-008): scanning the resource's QR code, from the booking, or at a site's kiosk.</summary>
public static class CheckInMethods
{
    public const string Qr = "qr";
    public const string Booking = "booking";
    public const string Kiosk = "kiosk";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal) { Qr, Booking, Kiosk };
}

// ---- Views ----

public sealed record BookingRights(bool Book, bool Manage, bool Report, IReadOnlyList<string> Kinds);

public sealed record SiteView(Guid Id, IReadOnlyDictionary<string, string> Names, string? LocationKey, Guid? OrgUnitId, string TimeZone, TimeOnly OpensAt, TimeOnly ClosesAt,
    string Status, bool Manages, int Version);

public sealed record FloorView(Guid Id, IReadOnlyDictionary<string, string> Names, int Sort, IReadOnlyList<ZoneView> Zones);

public sealed record ZoneView(Guid Id, IReadOnlyDictionary<string, string> Names);

public sealed record ResourceView(
    Guid Id,
    Guid SiteId,
    Guid? FloorId,
    Guid? ZoneId,
    string Kind,
    IReadOnlyDictionary<string, string> Names,
    int? Capacity,
    IReadOnlyList<string> Attributes,
    Guid? FixedUserId,
    string? Mailbox,
    bool CheckIn,
    bool NeedsApproval,
    string Status,
    string? QrCode,
    int Version,
    IReadOnlyList<Guid> ApproverIds);

/// <summary>A site with its floors, zones and resources, for facilities administrators.</summary>
public sealed record SiteDetail(SiteView Site, IReadOnlyList<FloorView> Floors, IReadOnlyList<ResourceView> Resources);

/// <summary>The rule that applies, and where it comes from: resource, site, tenant or default.</summary>
public sealed record RuleView(string Source, IReadOnlyList<Guid> UnitIds, int AdvanceDays, int MinMinutes, int MaxMinutes, bool WorkingHours, int BufferMinutes, int MaxActive,
    bool Overnight, int SeriesDays, int Version);

public sealed record SettingsView(int SlotMinutes, int CheckInBeforeMinutes, int CheckInGraceMinutes, int ApprovalHoldHours, int NoShowLimit, int NoShowWindowDays,
    bool NoShowRestrict, int NoShowRestrictDays, int NoShowAdvanceDays, bool ShowNames, int Version);

/// <summary>One printed label: the resource and the opaque code its QR image carries (US1 scenario 1).</summary>
public sealed record QrLabel(Guid ResourceId, string Kind, IReadOnlyDictionary<string, string> Names, IReadOnlyDictionary<string, string>? Floor,
    IReadOnlyDictionary<string, string>? Zone, string Code, string Link);

/// <summary>A resource free for the asked time that the viewer may book (FR-004).</summary>
public sealed record SearchHit(Guid Id, string Kind, IReadOnlyDictionary<string, string> Names, int? Capacity, IReadOnlyList<string> Attributes,
    IReadOnlyDictionary<string, string>? Floor, IReadOnlyDictionary<string, string>? Zone, bool NeedsApproval);

public sealed record SearchResult(Guid SiteId, string TimeZone, DateTimeOffset StartsAt, DateTimeOffset EndsAt, IReadOnlyList<SearchHit> Resources);

/// <summary>A taken slot on a resource's day. Holder is shown only where the tenant lets colleagues see who sits where.</summary>
public sealed record BusySlot(DateTimeOffset StartsAt, DateTimeOffset EndsAt, string Status, string? Holder);

public sealed record AvailabilityView(Guid ResourceId, DateOnly Date, string TimeZone, TimeOnly OpensAt, TimeOnly ClosesAt, bool WorkingDay, IReadOnlyList<BusySlot> Busy);

public sealed record SkippedDate(DateOnly Date, string Reason);

public sealed record BookingView(
    Guid Id,
    Guid ResourceId,
    string Kind,
    IReadOnlyDictionary<string, string> ResourceNames,
    Guid SiteId,
    IReadOnlyDictionary<string, string> SiteNames,
    string TimeZone,
    DateTimeOffset StartsAt,
    DateTimeOffset EndsAt,
    DateOnly LocalDate,
    TimeOnly LocalStart,
    TimeOnly LocalEnd,
    string Status,
    string? Title,
    Guid HolderId,
    Guid BookedBy,
    Guid? SeriesId,
    DateTimeOffset? CheckedInAt,
    string? EndReason,
    bool CanCancel,
    int Version,
    DateTimeOffset? HoldUntil,
    string? DecisionNote,
    string? CheckInMethod,
    DateTimeOffset? CheckInFrom,
    DateTimeOffset? CheckInUntil,
    bool CanCheckIn);

/// <summary>
/// What scanning a resource's code finds (FR-008): the resource, and the scanning person's booking of it whose check-in
/// window is open, or the next one today.
/// </summary>
public sealed record ScanView(Guid ResourceId, string Kind, IReadOnlyDictionary<string, string> ResourceNames, Guid SiteId, IReadOnlyDictionary<string, string> SiteNames,
    string TimeZone, bool CheckIn, BookingView? Booking);

/// <summary>A site's kiosk for the signed-in person: their bookings there today that they can check in to or did.</summary>
public sealed record KioskView(Guid SiteId, IReadOnlyDictionary<string, string> SiteNames, string TimeZone, IReadOnlyList<BookingView> Bookings);

/// <summary>What a booking request made: the bookings, and for a series the dates left out with why (FR-006).</summary>
public sealed record BookResult(IReadOnlyList<BookingView> Bookings, Guid? SeriesId, IReadOnlyList<SkippedDate> Skipped);

// ---- Rows ----

internal static class Names
{
    public const int MaxLength = 120;

    public static Dictionary<string, string> Read(NpgsqlDataReader r, int ordinal) =>
        r.IsDBNull(ordinal) ? [] : JsonSerializer.Deserialize<Dictionary<string, string>>(r.GetString(ordinal), JsonDefaults.Options) ?? [];

    /// <summary>The name in the locale, else English, else any.</summary>
    public static string Pick(IReadOnlyDictionary<string, string> names, string locale) =>
        names.TryGetValue(locale, out var n) ? n : names.TryGetValue("en", out var e) ? e : names.Values.FirstOrDefault() ?? "";
}

internal sealed record SiteRow(Guid Id, Dictionary<string, string> Names, string? LocationKey, Guid? OrgUnitId, string TimeZone, TimeOnly OpensAt, TimeOnly ClosesAt, string Status,
    int Version)
{
    public const string Columns = "id, names::text, location_key, org_unit_id, time_zone, opens_at, closes_at, status, row_version";

    public static SiteRow Read(NpgsqlDataReader r) => new(r.GetGuid(0), Booking.Names.Read(r, 1), r.Get<string>("location_key"), r.Get<Guid?>("org_unit_id"), r.GetString(4),
        r.GetFieldValue<TimeOnly>(5), r.GetFieldValue<TimeOnly>(6), r.GetString(7), r.GetInt32(8));

    public TimeZoneInfo Zone => TimeZoneInfo.FindSystemTimeZoneById(TimeZone);
}

internal sealed record ResourceRow(Guid Id, Guid SiteId, Guid? FloorId, Guid? ZoneId, string Kind, Dictionary<string, string> Names, int? Capacity, string[] Attributes,
    Guid? FixedUserId, string? Mailbox, bool CheckIn, bool NeedsApproval, string QrCode, string Status, int Version, Guid[] ApproverIds)
{
    public const int Count = 16;

    public const string Columns =
        "id, site_id, floor_id, zone_id, kind, names::text, capacity, attributes, fixed_user_id, mailbox, check_in, needs_approval, qr_code, status, row_version, approver_ids";

    public static ResourceRow Read(NpgsqlDataReader r) => new(r.GetGuid(0), r.GetGuid(1), r.Get<Guid?>("floor_id"), r.Get<Guid?>("zone_id"), r.GetString(4), Booking.Names.Read(r, 5),
        r.Get<int?>("capacity"), r.GetFieldValue<string[]>(7), r.Get<Guid?>("fixed_user_id"), r.Get<string>("mailbox"), r.GetBoolean(10), r.GetBoolean(11), r.GetString(12),
        r.GetString(13), r.GetInt32(14), r.GetFieldValue<Guid[]>(15));

    public bool Active => Status == "active";
}

internal sealed record BookingRow(Guid Id, Guid ResourceId, Guid HolderId, Guid BookedBy, Guid? SeriesId, DateTimeOffset StartsAt, DateTimeOffset EndsAt, string? Title,
    string Status, DateTimeOffset? CheckedInAt, string? EndReason, int Version, DateTimeOffset? HoldUntil, Guid? InstanceId, string? DecisionNote, string? CheckInMethod,
    DateTimeOffset CreatedAt)
{
    public const int Count = 17;

    public const string Columns =
        "id, resource_id, holder_id, booked_by, series_id, starts_at, ends_at, title, status, checked_in_at, end_reason, row_version, hold_until, instance_id, decision_note, "
        + "check_in_method, created_at";

    public static BookingRow Read(NpgsqlDataReader r) => new(r.GetGuid(0), r.GetGuid(1), r.GetGuid(2), r.GetGuid(3), r.Get<Guid?>("series_id"),
        r.GetFieldValue<DateTimeOffset>(5), r.GetFieldValue<DateTimeOffset>(6), r.Get<string>("title"), r.GetString(8), r.Get<DateTimeOffset?>("checked_in_at"),
        r.Get<string>("end_reason"), r.GetInt32(11), r.Get<DateTimeOffset?>("hold_until"), r.Get<Guid?>("instance_id"), r.Get<string>("decision_note"),
        r.Get<string>("check_in_method"), r.GetFieldValue<DateTimeOffset>(16));

    public bool Live => BookingStatuses.Live.Contains(Status);
}
