using System.Globalization;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Npgsql;
using TeamBeats.Audit.Contracts;
using TeamBeats.Authorization.Contracts;
using TeamBeats.Entitlements.Contracts;
using TeamBeats.Modules.Identity.Contracts;
using TeamBeats.Modules.Notifications.Contracts;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Primitives;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.Booking;

/// <param name="Version">Required to change a visit; the version the host last saw.</param>
public sealed record VisitRequest(Guid SiteId, DateOnly? Date, TimeOnly? Start, TimeOnly? End, string? Name, string? Email, string? Company, Guid? BookingId = null,
    string? Locale = null, int? Version = null);

public sealed record CheckInCodeRequest(string? Code);

/// <summary>
/// A visit as its host or reception sees it. The visitor's name, email and company are null once the visit is 30 days
/// old (FR-025). The code is shown to the host only; reception checks in by the code the visitor brings.
/// </summary>
public sealed record VisitView(Guid Id, Guid SiteId, IReadOnlyDictionary<string, string> SiteNames, string TimeZone, DateOnly Date, DateTimeOffset StartsAt, DateTimeOffset EndsAt,
    string? Name, string? Email, string? Company, Guid? BookingId, string Status, string? EndReason, string EmailState, DateTimeOffset? ArrivedAt, DateTimeOffset? LeftAt,
    string? Code, Guid HostId, string? HostName, int Version);

/// <summary>A site the viewer works reception for.</summary>
public sealed record ReceptionSite(Guid Id, IReadOnlyDictionary<string, string> Names, string TimeZone);

/// <summary>The visitors of a site on a day (US8 scenario 2), by time.</summary>
public sealed record ReceptionDay(Guid SiteId, IReadOnlyDictionary<string, string> SiteNames, string TimeZone, DateOnly Date, bool Today, IReadOnlyList<VisitView> Visits);

public static class VisitStatuses
{
    public const string Expected = "expected";
    public const string Arrived = "arrived";
    public const string Left = "left";
    public const string Cancelled = "cancelled";
}

/// <summary>
/// Visitor booking (spec 031 R3 US8, FR-022 to FR-025). Anyone who may book registers visitors for a site and time,
/// optionally with one of their own bookings there, and changes or cancels their own visits. The visitor gets an email
/// with an opaque 8 character code through the 015 external email contract, and another when the visit is changed or
/// cancelled. Reception (<c>booking.reception</c> in the site's scope) sees the day's visitors and checks them in by
/// code, which works only on the visit day, or from the list; the host is told on arrival. The maintenance clock retries
/// emails and clears the visitor's details 30 days after the visit. Audit entries never carry visitor details.
/// </summary>
public sealed partial class Visits(
    IDbSession session,
    ITenantContext context,
    IAuthorizationService authz,
    IEntitlementService entitlements,
    Facilities facilities,
    IUserDirectory users,
    ITenantSettingsReader tenantSettings,
    IExternalEmail email,
    INotificationSender notifications,
    IAuditWriter audit,
    IClock clock)
{
    public const string RegisteredTemplate = "booking.visit.registered";
    public const string ChangedTemplate = "booking.visit.changed";
    public const string CancelledTemplate = "booking.visit.cancelled";
    public const string ArrivedTemplate = "booking.visitor_arrived";
    public const string VisitType = "booking_visit";
    public const int MaxPerHostPerDay = 20;
    public const int MinMinutes = 15;
    public const int MaxHours = 12;
    public const int MaxDaysAhead = 90;
    public const int RetentionDays = 30;
    public const int MaxEmailAttempts = 5;
    public const int CodeLength = 8;

    /// <summary>Letters and digits that cannot be mistaken for each other when read aloud or typed (no I, O, 0 or 1).</summary>
    internal const string CodeAlphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";

    [GeneratedRegex(@"^[^@\s]{1,64}@[^@\s]{1,190}\.[^@\s]{2,}$")]
    private static partial Regex EmailPattern();

    private const string Columns =
        """
        v.id, v.site_id, s.names::text, s.time_zone, v.day, v.starts_at, v.ends_at, v.visitor_name, v.visitor_email, v.visitor_company, v.booking_id, v.status,
        v.end_reason, v.email_state, v.arrived_at, v.left_at, v.code, v.host_id, v.row_version, v.locale, s.org_unit_id
        """;

    private sealed record Row(Guid Id, Guid SiteId, Dictionary<string, string> SiteNames, string TimeZone, DateOnly Day, DateTimeOffset StartsAt, DateTimeOffset EndsAt,
        string? Name, string? Email, string? Company, Guid? BookingId, string Status, string? EndReason, string EmailState, DateTimeOffset? ArrivedAt, DateTimeOffset? LeftAt,
        string? Code, Guid HostId, int Version, string? Locale, Guid? OrgUnitId);

    private static Row Read(NpgsqlDataReader r) => new(r.GetGuid(0), r.GetGuid(1), Names.Read(r, 2), r.GetString(3), r.GetFieldValue<DateOnly>(4), r.GetFieldValue<DateTimeOffset>(5),
        r.GetFieldValue<DateTimeOffset>(6), r.Get<string>("visitor_name"), r.Get<string>("visitor_email"), r.Get<string>("visitor_company"), r.Get<Guid?>("booking_id"),
        r.GetString(11), r.Get<string>("end_reason"), r.GetString(13), r.Get<DateTimeOffset?>("arrived_at"), r.Get<DateTimeOffset?>("left_at"), r.Get<string>("code"),
        r.GetGuid(17), r.GetInt32(18), r.Get<string>("locale"), r.Get<Guid?>("org_unit_id"));

    private Task<Row?> RowAsync(Guid id, CancellationToken ct) =>
        session.QuerySingleOrDefaultAsync($"select {Columns} from booking.visit v join booking.site s on s.id = v.site_id where v.id = @id", new { id }, Read, ct);

    private static VisitView View(Row v, bool showCode, string? hostName) => new(v.Id, v.SiteId, v.SiteNames, v.TimeZone, v.Day, v.StartsAt, v.EndsAt, v.Name, v.Email, v.Company,
        v.BookingId, v.Status, v.EndReason, v.EmailState, v.ArrivedAt, v.LeftAt, showCode && v.Status == VisitStatuses.Expected ? v.Code : null, v.HostId, hostName, v.Version);

    internal static string Link() => "/booking/visits";

    private async Task RequireVisitorsAsync(CancellationToken ct)
    {
        await facilities.RequireAnyAsync(ct);
        await entitlements.DemandAsync(Capabilities.BookingVisitors, ct);
    }

    private async Task<Guid> RequireHostAsync(CancellationToken ct)
    {
        await RequireVisitorsAsync(ct);
        await authz.DemandAsync(Permissions.BookingBook, AuthzTarget.Tenant, ct);
        return context.RequireUser();
    }

    internal static DateOnly LocalDay(DateTimeOffset instant, TimeZoneInfo zone) => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(instant, zone).DateTime);

    // ---- Hosts ----

    /// <summary>The host's visits from today on, and those of the last 30 days.</summary>
    public async Task<IReadOnlyList<VisitView>> MineAsync(CancellationToken ct)
    {
        var host = await RequireHostAsync(ct);
        var rows = await session.QueryAsync(
            $"select {Columns} from booking.visit v join booking.site s on s.id = v.site_id where v.host_id = @host and v.ends_at >= @since order by v.starts_at, v.id limit 500",
            new { host, since = clock.UtcNow.AddDays(-RetentionDays) }, Read, ct);
        return rows.Select(v => View(v, true, null)).ToList();
    }

    public async Task<VisitView> GetAsync(Guid id, CancellationToken ct)
    {
        var host = await RequireHostAsync(ct);
        var row = await RowAsync(id, ct);
        return row is not null && row.HostId == host ? View(row, true, null) : throw AppException.NotFound();
    }

    private sealed record Checked(SiteRow Site, DateOnly Day, DateTimeOffset StartsAt, DateTimeOffset EndsAt, string Name, string Email, string? Company, string? Locale);

    private async Task<Checked> ValidateAsync(VisitRequest request, Guid host, Guid? existing, CancellationToken ct)
    {
        var site = await facilities.ActiveSiteAsync(request.SiteId, ct);
        var errors = new ValidationErrors();
        var name = request.Name?.Trim() ?? "";
        var address = request.Email?.Trim() ?? "";
        var company = string.IsNullOrWhiteSpace(request.Company) ? null : request.Company.Trim();
        if (name.Length is 0 or > 200) errors.Add("name", "validation.booking.visitor_name");
        if (address.Length > 320 || !EmailPattern().IsMatch(address)) errors.Add("email", "validation.booking.visitor_email");
        if (company is { Length: > 200 }) errors.Add("company", "validation.booking.visitor_company");
        var locale = request.Locale is { } l && Primitives.Locales.IsSupported(l) ? l : null;
        if (request.Date is not { } date || request.Start is not { } start || request.End is not { } end)
        {
            errors.Add("date", "validation.booking.visit_time");
            errors.ThrowIfAny();
            throw new InvalidOperationException();
        }
        var zone = site.Zone;
        var minutes = (end - start).TotalMinutes;
        if (end <= start || minutes < MinMinutes || minutes > MaxHours * 60)
        {
            errors.Add("end", "validation.booking.visit_length");
        }
        var now = clock.UtcNow;
        var today = LocalDay(now, zone);
        var startsAt = Bookings.At(date, start, zone);
        var endsAt = Bookings.At(date, end, zone);
        if (date < today || endsAt <= now || date.DayNumber - today.DayNumber > MaxDaysAhead)
        {
            errors.Add("date", "validation.booking.visit_window");
        }
        errors.ThrowIfAny();
        if (request.BookingId is { } bookingId && !await session.ScalarAsync<bool>(
                """
                select exists (select 1 from booking.booking b join booking.resource r on r.id = b.resource_id
                               where b.id = @bookingId and b.holder_id = @host and r.site_id = @site and b.status = any(@live))
                """, new { bookingId, host, site = site.Id, live = BookingStatuses.Live }, ct))
        {
            throw AppException.Validation("bookingId", "validation.booking.visit_booking");
        }
        var count = await session.ScalarAsync<int>(
            "select count(*)::int from booking.visit where host_id = @host and day = @date and status <> 'cancelled' and (@existing::uuid is null or id <> @existing)",
            new { host, date, existing }, ct);
        if (count >= MaxPerHostPerDay)
        {
            throw AppException.Conflict("conflict.booking.visit_limit");
        }
        return new Checked(site, date, startsAt, endsAt, name, address, company, locale);
    }

    private async Task<string> NewCodeAsync(CancellationToken ct)
    {
        for (var attempt = 0; attempt < 10; attempt++)
        {
            var code = string.Create(CodeLength, 0, (span, _) =>
            {
                for (var i = 0; i < span.Length; i++) span[i] = CodeAlphabet[RandomNumberGenerator.GetInt32(CodeAlphabet.Length)];
            });
            if (!await session.ScalarAsync<bool>("select exists (select 1 from booking.visit where code = @code)", new { code }, ct))
            {
                return code;
            }
        }
        throw new InvalidOperationException("No free visit code after ten tries.");
    }

    /// <summary>Registers a visitor (US8 scenario 1) and emails them their code.</summary>
    public async Task<VisitView> RegisterAsync(VisitRequest request, CancellationToken ct)
    {
        var host = await RequireHostAsync(ct);
        var v = await ValidateAsync(request, host, null, ct);
        var id = Guid.NewGuid();
        var now = clock.UtcNow;
        await session.ExecAsync(
            """
            insert into booking.visit (tenant_id, id, site_id, host_id, booking_id, day, starts_at, ends_at, visitor_name, visitor_email, visitor_company, locale, code,
              status, email_kind, email_state, email_next_at, created_at, updated_at)
            values (platform.current_tenant_id(), @id, @site, @host, @bookingId, @day, @startsAt, @endsAt, @name, @email, @company, @locale, @code,
              'expected', 'registered', 'pending', @now, @now, @now)
            """, new
            {
                id, site = v.Site.Id, host, bookingId = request.BookingId, day = v.Day, startsAt = v.StartsAt, endsAt = v.EndsAt, name = v.Name, email = v.Email,
                company = v.Company, locale = v.Locale, code = await NewCodeAsync(ct), now,
            }, ct);
        await audit.WriteAsync(new AuditEntry(AuditActions.BookingVisitRegistered, VisitType, id.ToString(), TargetOrgUnitId: v.Site.OrgUnitId,
            Changes: new { site = v.Site.Id, change = "registered" }), ct);
        await SendAsync(id, ct);
        return View((await RowAsync(id, ct))!, true, null);
    }

    /// <summary>Changes the host's own expected visit; the visitor is told (US8 scenario 3). The code stays.</summary>
    public async Task<VisitView> ChangeAsync(Guid id, VisitRequest request, CancellationToken ct)
    {
        var host = await RequireHostAsync(ct);
        var row = await RowAsync(id, ct);
        if (row is null || row.HostId != host)
        {
            throw AppException.NotFound();
        }
        if (row.Status != VisitStatuses.Expected || row.EndsAt <= clock.UtcNow)
        {
            throw AppException.Conflict("conflict.booking.visit_closed");
        }
        if (request.Version != row.Version)
        {
            throw AppException.Conflict("conflict.booking.changed");
        }
        if (request.SiteId != row.SiteId)
        {
            throw AppException.Validation("siteId", "validation.booking.visit_site");
        }
        var v = await ValidateAsync(request, host, id, ct);
        await session.ExecAsync(
            """
            update booking.visit set booking_id = @bookingId, day = @day, starts_at = @startsAt, ends_at = @endsAt, visitor_name = @name, visitor_email = @email,
              visitor_company = @company, locale = coalesce(@locale, locale), email_kind = 'changed', email_state = 'pending', email_attempts = 0, email_next_at = @now,
              updated_at = @now, row_version = row_version + 1
            where id = @id
            """, new { id, bookingId = request.BookingId, day = v.Day, startsAt = v.StartsAt, endsAt = v.EndsAt, name = v.Name, email = v.Email, company = v.Company, locale = v.Locale, now = clock.UtcNow }, ct);
        await audit.WriteAsync(new AuditEntry(AuditActions.BookingVisitRegistered, VisitType, id.ToString(), TargetOrgUnitId: row.OrgUnitId,
            Changes: new { site = row.SiteId, change = "changed" }), ct);
        await SendAsync(id, ct);
        return View((await RowAsync(id, ct))!, true, null);
    }

    /// <summary>Cancels the host's own expected visit; the visitor is told (US8 scenario 3).</summary>
    public async Task<VisitView> CancelAsync(Guid id, CancellationToken ct)
    {
        var host = await RequireHostAsync(ct);
        var row = await RowAsync(id, ct);
        if (row is null || row.HostId != host)
        {
            throw AppException.NotFound();
        }
        if (row.Status == VisitStatuses.Cancelled)
        {
            return View(row, true, null);
        }
        if (row.Status != VisitStatuses.Expected || row.EndsAt <= clock.UtcNow)
        {
            throw AppException.Conflict("conflict.booking.visit_closed");
        }
        await CancelRowAsync(row, "host", ct);
        return View((await RowAsync(id, ct))!, true, null);
    }

    private async Task CancelRowAsync(Row row, string reason, CancellationToken ct)
    {
        await session.ExecAsync(
            """
            update booking.visit set status = 'cancelled', end_reason = @reason, code = null, email_kind = 'cancelled', email_state = 'pending', email_attempts = 0,
              email_next_at = @now, updated_at = @now, row_version = row_version + 1
            where id = @id
            """, new { id = row.Id, reason, now = clock.UtcNow }, ct);
        await audit.WriteAsync(new AuditEntry(AuditActions.BookingVisitCancelled, VisitType, row.Id.ToString(), TargetOrgUnitId: row.OrgUnitId,
            Changes: new { site = row.SiteId, reason }), ct);
        await SendAsync(row.Id, ct);
    }

    /// <summary>A host who left the company: their future visits are cancelled and the visitors told (section 4 edge case).</summary>
    internal async Task<int> CancelForLeaverAsync(Guid host, CancellationToken ct)
    {
        var rows = await session.QueryAsync(
            $"select {Columns} from booking.visit v join booking.site s on s.id = v.site_id where v.host_id = @host and v.status = 'expected' and v.ends_at > @now",
            new { host, now = clock.UtcNow }, Read, ct);
        foreach (var row in rows)
        {
            await CancelRowAsync(row, "left_company", ct);
        }
        return rows.Count;
    }

    // ---- Emails ----

    /// <summary>
    /// Sends the visit's pending email now. A relay that is down leaves it pending for the clock to try again, up to
    /// <see cref="MaxEmailAttempts"/> times; a refused address is kept as bounced so the host sees it.
    /// </summary>
    private async Task SendAsync(Guid id, CancellationToken ct)
    {
        var row = await session.QuerySingleOrDefaultAsync(
            $"select {Columns}, v.email_kind, v.email_attempts from booking.visit v join booking.site s on s.id = v.site_id where v.id = @id and v.email_state = 'pending'",
            new { id }, r => (Row: Read(r), Kind: r.GetString(21), Attempts: r.GetInt32(22)), ct);
        if (row.Row is null || row.Row.Email is null)
        {
            return;
        }
        var v = row.Row;
        var locale = v.Locale ?? (await tenantSettings.GetLocaleSettingsAsync(ct)).DefaultLocale;
        var zone = TimeZoneInfo.FindSystemTimeZoneById(v.TimeZone);
        var start = TimeZoneInfo.ConvertTime(v.StartsAt, zone);
        var end = TimeZoneInfo.ConvertTime(v.EndsAt, zone);
        var parameters = new Dictionary<string, string>
        {
            ["visitor"] = v.Name ?? "",
            ["site"] = Names.Pick(v.SiteNames, locale),
            ["host"] = (await users.GetAsync(v.HostId, ct))?.DisplayName ?? "",
            ["date"] = start.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            ["start"] = start.ToString("HH:mm", CultureInfo.InvariantCulture),
            ["end"] = end.ToString("HH:mm", CultureInfo.InvariantCulture),
            ["timeZone"] = v.TimeZone,
            ["code"] = v.Code ?? "",
        };
        var template = row.Kind switch { "changed" => ChangedTemplate, "cancelled" => CancelledTemplate, _ => RegisteredTemplate };
        string outcome;
        try
        {
            outcome = await email.SendAsync(new ExternalEmailRequest(v.Email, template, "/", parameters, locale), ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            outcome = ExternalEmailOutcomes.Retry;
        }
        var attempts = row.Attempts + 1;
        var state = outcome switch
        {
            ExternalEmailOutcomes.Sent => "sent",
            ExternalEmailOutcomes.Bounced => "bounced",
            _ => attempts >= MaxEmailAttempts ? "failed" : "pending",
        };
        await session.ExecAsync("update booking.visit set email_state = @state, email_attempts = @attempts, email_next_at = @next where id = @id",
            new { id, state, attempts, next = state == "pending" ? clock.UtcNow.AddMinutes(5 * attempts) : (DateTimeOffset?)null }, ct);
    }

    // ---- Reception ----

    private async Task<bool> ReceivesAsync(Guid? orgUnitId, CancellationToken ct) =>
        (await authz.CheckAsync(Permissions.BookingReception, Facilities.Scope(orgUnitId), ct)).Allowed;

    private async Task<SiteRow> ReceptionSiteAsync(Guid siteId, CancellationToken ct)
    {
        await RequireVisitorsAsync(ct);
        var site = await facilities.ActiveSiteAsync(siteId, ct);
        if (!await ReceivesAsync(site.OrgUnitId, ct))
        {
            throw await authz.HoldsAnywhereAsync(Permissions.BookingReception, ct) ? AppException.NotFound() : AppException.Denied("authz.denied.booking_reception");
        }
        return site;
    }

    /// <summary>The active sites the viewer works reception for.</summary>
    public async Task<IReadOnlyList<ReceptionSite>> ReceptionSitesAsync(CancellationToken ct)
    {
        await RequireVisitorsAsync(ct);
        if (!await authz.HoldsAnywhereAsync(Permissions.BookingReception, ct))
        {
            throw AppException.Denied("authz.denied.booking_reception");
        }
        var sites = await session.QueryAsync($"select {SiteRow.Columns} from booking.site where status = 'active' order by created_at, id", null, SiteRow.Read, ct);
        var result = new List<ReceptionSite>();
        foreach (var s in sites)
        {
            if (await ReceivesAsync(s.OrgUnitId, ct))
            {
                result.Add(new ReceptionSite(s.Id, s.Names, s.TimeZone));
            }
        }
        return result;
    }

    /// <summary>The site's visitors on a day, today in the site's time zone unless another is asked for (US8 scenario 2).</summary>
    public async Task<ReceptionDay> DayAsync(Guid siteId, DateOnly? date, CancellationToken ct)
    {
        var site = await ReceptionSiteAsync(siteId, ct);
        var today = LocalDay(clock.UtcNow, site.Zone);
        var day = date ?? today;
        var rows = await session.QueryAsync(
            $"select {Columns} from booking.visit v join booking.site s on s.id = v.site_id where v.site_id = @siteId and v.day = @day order by v.starts_at, v.visitor_name, v.id",
            new { siteId, day }, Read, ct);
        var hosts = await users.GetManyAsync(rows.Select(r => r.HostId).Distinct().ToList(), ct);
        return new ReceptionDay(site.Id, site.Names, site.TimeZone, day, day == today,
            rows.Select(r => View(r, false, hosts.TryGetValue(r.HostId, out var u) ? u.DisplayName : null)).ToList());
    }

    /// <summary>Checks a visitor in by the code they bring. A code works only on the visit day, at the visit's site.</summary>
    public async Task<VisitView> CheckInByCodeAsync(Guid siteId, CheckInCodeRequest request, CancellationToken ct)
    {
        var site = await ReceptionSiteAsync(siteId, ct);
        var code = (request.Code ?? "").Trim().Replace(" ", "", StringComparison.Ordinal).Replace("-", "", StringComparison.Ordinal).ToUpperInvariant();
        if (code.Length != CodeLength)
        {
            throw AppException.Validation("code", "validation.booking.visit_code");
        }
        var row = await session.QuerySingleOrDefaultAsync(
            $"select {Columns} from booking.visit v join booking.site s on s.id = v.site_id where v.code = @code and v.site_id = @siteId", new { code, siteId }, Read, ct);
        if (row is null || row.Day != LocalDay(clock.UtcNow, site.Zone))
        {
            throw AppException.Conflict("conflict.booking.visit_code");
        }
        return await ArriveAsync(row, ct);
    }

    /// <summary>Checks a visitor in from the day's list.</summary>
    public async Task<VisitView> CheckInAsync(Guid id, CancellationToken ct)
    {
        var row = await RowAsync(id, ct) ?? throw AppException.NotFound();
        var site = await ReceptionSiteAsync(row.SiteId, ct);
        if (row.Day != LocalDay(clock.UtcNow, site.Zone))
        {
            throw AppException.Conflict("conflict.booking.visit_not_today");
        }
        return await ArriveAsync(row, ct);
    }

    private async Task<VisitView> ArriveAsync(Row row, CancellationToken ct)
    {
        if (row.Status == VisitStatuses.Arrived)
        {
            return await ReceptionViewAsync(row.Id, ct);
        }
        if (row.Status != VisitStatuses.Expected)
        {
            throw AppException.Conflict("conflict.booking.visit_closed");
        }
        var now = clock.UtcNow;
        await session.ExecAsync(
            "update booking.visit set status = 'arrived', arrived_at = @now, arrived_by = @user, updated_at = @now, row_version = row_version + 1 where id = @id",
            new { id = row.Id, now, user = context.RequireUser() }, ct);
        await audit.WriteAsync(new AuditEntry(AuditActions.BookingVisitorCheckedIn, VisitType, row.Id.ToString(), TargetOrgUnitId: row.OrgUnitId,
            Changes: new { site = row.SiteId, change = "arrived" }), ct);
        var locale = (await tenantSettings.GetLocaleSettingsAsync(ct)).DefaultLocale;
        await notifications.SendAsync(new NotificationRequest([row.HostId], NotificationCategories.Bookings, ArrivedTemplate, Link(),
            new Dictionary<string, string> { ["visitor"] = row.Name ?? "", ["site"] = Names.Pick(row.SiteNames, locale) }, DedupeKey: $"booking:visit:{row.Id:N}:arrived"), ct);
        return await ReceptionViewAsync(row.Id, ct);
    }

    /// <summary>Marks an arrived visitor as gone.</summary>
    public async Task<VisitView> CheckOutAsync(Guid id, CancellationToken ct)
    {
        var row = await RowAsync(id, ct) ?? throw AppException.NotFound();
        await ReceptionSiteAsync(row.SiteId, ct);
        if (row.Status == VisitStatuses.Left)
        {
            return await ReceptionViewAsync(id, ct);
        }
        if (row.Status != VisitStatuses.Arrived)
        {
            throw AppException.Conflict("conflict.booking.visit_not_arrived");
        }
        var now = clock.UtcNow;
        await session.ExecAsync("update booking.visit set status = 'left', left_at = @now, updated_at = @now, row_version = row_version + 1 where id = @id", new { id, now }, ct);
        await audit.WriteAsync(new AuditEntry(AuditActions.BookingVisitorCheckedIn, VisitType, id.ToString(), TargetOrgUnitId: row.OrgUnitId,
            Changes: new { site = row.SiteId, change = "left" }), ct);
        return await ReceptionViewAsync(id, ct);
    }

    private async Task<VisitView> ReceptionViewAsync(Guid id, CancellationToken ct)
    {
        var row = (await RowAsync(id, ct))!;
        return View(row, false, (await users.GetAsync(row.HostId, ct))?.DisplayName);
    }

    // ---- Clock ----

    /// <summary>Retries pending emails and clears visitor details 30 days after the visit (FR-025). Returns how many were cleared.</summary>
    internal async Task<(int Retried, int Purged)> RunDueAsync(DateTimeOffset now, CancellationToken ct)
    {
        var due = await session.QueryAsync(
            "select id from booking.visit where email_state = 'pending' and email_next_at <= @now order by email_next_at limit @batch",
            new { now, batch = BookingMaintenance.Batch }, r => r.GetGuid(0), ct);
        foreach (var id in due)
        {
            await SendAsync(id, ct);
        }
        var purged = await session.ExecAsync(
            """
            update booking.visit set visitor_name = null, visitor_email = null, visitor_company = null, code = null, locale = null, purged_at = @now,
              email_state = case when email_state = 'pending' then 'failed' else email_state end, email_next_at = null
            where id in (select id from booking.visit where purged_at is null and ends_at < @cutoff order by ends_at limit @batch)
            """, new { now, cutoff = now.AddDays(-RetentionDays), batch = BookingMaintenance.Batch }, ct);
        return (due.Count, purged);
    }

    /// <summary>Visit counts for a site and range, for reports (FR-025): counts only, never names.</summary>
    internal async Task<VisitCounts?> CountsAsync(Guid siteId, DateOnly from, DateOnly to, CancellationToken ct)
    {
        if (!await entitlements.IsEnabledAsync(Capabilities.BookingVisitors, ct))
        {
            return null;
        }
        return await session.QuerySingleOrDefaultAsync(
            """
            select count(*) filter (where status <> 'cancelled')::int, count(*) filter (where status in ('arrived', 'left'))::int, count(*) filter (where status = 'cancelled')::int
            from booking.visit where site_id = @siteId and day between @from and @to
            """, new { siteId, from, to }, r => new VisitCounts(r.GetInt32(0), r.GetInt32(1), r.GetInt32(2)), ct);
    }
}

/// <summary>Visits in a report range: expected (not cancelled), arrived (checked in) and cancelled.</summary>
public sealed record VisitCounts(int Expected, int Arrived, int Cancelled);
