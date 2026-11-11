using System.Security.Cryptography;
using TeamBeats.Audit.Contracts;
using TeamBeats.Authorization.Contracts;
using TeamBeats.Entitlements.Contracts;
using TeamBeats.Modules.Hr.Leave;
using TeamBeats.Modules.Integrations.Contracts;
using TeamBeats.Modules.Notifications.Contracts;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Primitives;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.Hr.Journeys;

public static class PreboardingStatuses
{
    public const string Scheduled = "scheduled";
    public const string Sent = "sent";
    public const string Cancelled = "cancelled";
    public const string Failed = "failed";
}

/// <summary>The journeys setting (spec 022 FR-016): pre-boarding before the account exists, off until HR turns it on. Versioned.</summary>
public sealed record JourneySettingsView(bool PreboardingEnabled, int Version, DateTimeOffset? UpdatedAt, PreboardingCounts Preboarding, bool CanChange)
{
    public string ETag => JourneyTemplates.ETag(Version);
}

/// <summary>How many pre-boarding invitations wait, went out, were opened or could not be sent. Counts only, no names.</summary>
public sealed record PreboardingCounts(int Scheduled, int Sent, int Opened, int Failed);

public sealed record JourneySettingsRequest(bool? PreboardingEnabled);

/// <summary>A pre-boarding page: the start date and the pre-boarding steps of the template, nothing else (FR-016).</summary>
public sealed record PreboardingPageView(DateOnly StartDate, DateTimeOffset ExpiresAt, IReadOnlyList<PreboardingStepView> Steps);

public sealed record PreboardingStepView(string Key, IReadOnlyDictionary<string, string> Names, IReadOnlyDictionary<string, string> Content, DateOnly ShowsOn);

/// <summary>
/// Pre-boarding (spec 022 US4 scenario 3, FR-016). A hire HR knows before their account exists gets one email at the
/// personal address HR holds, on the date of the earliest pre-boarding step of the onboarding template that applies
/// to them, with a link to a page that shows only those steps' text. The link holds a random token, of which only a
/// hash is kept, and stops working at the end of the start date or 30 days after it was sent, whichever comes first.
/// The personal email is read from HR when the email goes, never stored here. Needs <c>hr.journeys</c> and the tenant
/// setting; a hire with an account reads the same steps in their own journey instead.
/// </summary>
public sealed class Preboarding(
    IDbSession session,
    ITenantContext context,
    IClock clock,
    IAuthorizationService authz,
    IEntitlementService entitlements,
    IAuditWriter audit,
    IOrganizationHierarchy hierarchy,
    ITenantSettingsReader tenantSettings,
    IHrRecords hr,
    IExternalEmail email,
    JourneyTemplates templates,
    LeaveLedger people)
{
    public const string InviteTemplate = "hr.preboarding_invite";
    public const int TokenBytes = 32;
    public const int BatchSize = 100;
    public const int MaxAttempts = 12;
    public static readonly TimeSpan LinkLifetime = TimeSpan.FromDays(30);
    public static readonly TimeSpan RetryEvery = TimeSpan.FromMinutes(15);

    private sealed record Row(Guid Id, string RecordId, Guid TemplateId, DateOnly StartDate, string Status, int Attempts);

    // ---- setting ----

    private async Task<(bool Enabled, int Version, DateTimeOffset? UpdatedAt)> SettingRowAsync(CancellationToken ct)
    {
        var row = await session.QuerySingleOrDefaultAsync("select preboarding_enabled, version, updated_at from hr.journey_settings", null,
            r => (Enabled: r.GetBoolean(0), Version: r.GetInt32(1), UpdatedAt: r.Get<DateTimeOffset?>("updated_at")), ct);
        return row == default ? (false, 0, null) : row;
    }

    internal async Task<bool> EnabledAsync(CancellationToken ct) => (await SettingRowAsync(ct)).Enabled;

    public async Task<JourneySettingsView> SettingsAsync(CancellationToken ct)
    {
        await entitlements.DemandAsync(Capabilities.HrJourneys, ct);
        var coverage = await authz.CoverageAsync(Permissions.HrManage, ct);
        if (!coverage.Any)
        {
            throw AppException.Denied();
        }
        var (enabled, version, updatedAt) = await SettingRowAsync(ct);
        var counts = await session.QuerySingleOrDefaultAsync(
            """
            select count(*) filter (where status = 'scheduled'), count(*) filter (where status = 'sent'),
                   count(*) filter (where opened_at is not null), count(*) filter (where status = 'failed')
            from hr.preboarding
            """, null, r => new PreboardingCounts((int)r.GetInt64(0), (int)r.GetInt64(1), (int)r.GetInt64(2), (int)r.GetInt64(3)), ct);
        return new JourneySettingsView(enabled, version, updatedAt, counts ?? new PreboardingCounts(0, 0, 0, 0), coverage.WholeTenant);
    }

    /// <summary>Changes the setting: a tenant-wide HR officer only, against the version read. Turning it off cancels invitations not yet sent.</summary>
    public async Task<JourneySettingsView> UpdateSettingsAsync(string? ifMatch, JourneySettingsRequest request, CancellationToken ct)
    {
        await entitlements.DemandAsync(Capabilities.HrJourneys, ct);
        if (!(await authz.CoverageAsync(Permissions.HrManage, ct)).WholeTenant)
        {
            throw AppException.Denied();
        }
        if (request.PreboardingEnabled is not { } enabled)
        {
            throw AppException.Validation("preboardingEnabled", "validation.required");
        }
        var (current, version, _) = await SettingRowAsync(ct);
        if (ifMatch != JourneyTemplates.ETag(version))
        {
            throw new AppException(ProblemCodes.Conflict, 409, "conflict.journey.changed", null,
                new Dictionary<string, object?> { ["etag"] = JourneyTemplates.ETag(version) });
        }
        var now = clock.UtcNow;
        var actor = context.RequireUser();
        await session.ExecAsync(
            """
            insert into hr.journey_settings (tenant_id, preboarding_enabled, version, updated_by, updated_at)
            values (platform.current_tenant_id(), @enabled, 1, @actor, @now)
            on conflict (tenant_id) do update set preboarding_enabled = @enabled, version = hr.journey_settings.version + 1, updated_by = @actor, updated_at = @now
            """, new { enabled, actor, now }, ct);
        if (current && !enabled)
        {
            await session.ExecAsync(
                "update hr.preboarding set status = 'cancelled', ended_reason = 'setting_off', updated_at = @now where status = 'scheduled'", new { now }, ct);
        }
        await audit.WriteAsync(new AuditEntry(AuditActions.HrJourneySettingsChanged, "journey_settings", "preboarding",
            Changes: new { preboardingEnabled = new { from = current, to = enabled } }), ct);
        return await SettingsAsync(ct);
    }

    // ---- scheduling ----

    /// <summary>
    /// An HR hire with no account yet: schedules the invitation when the setting is on, HR holds a personal email, and
    /// the onboarding template that applies has pre-boarding steps. A later hire event for the same record moves it.
    /// </summary>
    public async Task ScheduleAsync(string recordId, DateOnly startDate, CancellationToken ct)
    {
        if (!await entitlements.IsEnabledAsync(Capabilities.HrJourneys, ct) || !await EnabledAsync(ct)
            || await hr.PreHireAsync(recordId, ct) is not { } hire)
        {
            return;
        }
        var path = hire.OrgUnitId is { } unit && (await hierarchy.GetPathsAsync([unit], ct)).TryGetValue(unit, out var p) ? p : [];
        if (await templates.ApplicableAsync(JourneyStarts.Hire, null, path, ct) is not { } template
            || template.Steps.Where(s => s.Type == JourneyStepTypes.Preboarding).Select(s => (int?)s.OffsetDays).Min() is not { } first)
        {
            return;
        }
        var today = await people.TodayAsync(ct);
        if (startDate.DayNumber < today.DayNumber)
        {
            return;
        }
        var sendOn = startDate.AddDays(first);
        if (sendOn.DayNumber < today.DayNumber)
        {
            sendOn = today;
        }
        var now = clock.UtcNow;
        var existing = (await session.QueryAsync("select id, status from hr.preboarding where record_id = @recordId for update", new { recordId },
            r => (Id: r.GetGuid(0), Status: r.GetString(1)), ct)).SingleOrDefault();
        if (existing.Status == PreboardingStatuses.Sent)
        {
            // Already invited: the page shows the new date; the link keeps its own end.
            await session.ExecAsync("update hr.preboarding set start_date = @startDate, template_id = @template, updated_at = @now where id = @id",
                new { id = existing.Id, startDate, template = template.Id, now }, ct);
            return;
        }
        await session.ExecAsync(
            """
            insert into hr.preboarding (tenant_id, id, record_id, template_id, start_date, send_on, status, created_at, updated_at)
            values (platform.current_tenant_id(), @id, @recordId, @template, @startDate, @sendOn, 'scheduled', @now, @now)
            on conflict (tenant_id, record_id) do update set template_id = excluded.template_id, start_date = excluded.start_date, send_on = excluded.send_on,
              status = 'scheduled', attempts = 0, not_before = null, ended_reason = null, updated_at = excluded.updated_at
            """, new { id = Ids.New(), recordId, template = template.Id, startDate, sendOn, now }, ct);
    }

    /// <summary>
    /// Sends the invitations whose date has come, run by the journey worker. An invitation whose hire got an account,
    /// left, lost their personal email or started already is cancelled; a template without pre-boarding steps sends nothing.
    /// </summary>
    public async Task<int> SendDueAsync(CancellationToken ct)
    {
        if (!await entitlements.IsEnabledAsync(Capabilities.HrJourneys, ct) || !await EnabledAsync(ct))
        {
            return 0;
        }
        var today = await people.TodayAsync(ct);
        var now = clock.UtcNow;
        var due = await session.QueryAsync(
            """
            select id, record_id, template_id, start_date, status, attempts from hr.preboarding
            where status = 'scheduled' and send_on <= @today and (not_before is null or not_before <= @now)
            order by send_on, id limit @batch
            for update skip locked
            """, new { today, now, batch = BatchSize },
            r => new Row(r.GetGuid(0), r.GetString(1), r.GetGuid(2), DateOnly.FromDateTime(r.GetDateTime(3)), r.GetString(4), r.GetInt32(5)), ct);
        var zone = TimeZoneInfo.FindSystemTimeZoneById((await tenantSettings.GetLocaleSettingsAsync(ct)).TimeZone);
        var sent = 0;
        foreach (var row in due)
        {
            if (await CancelReasonAsync(row, today, ct) is { } reason)
            {
                await EndAsync(row.Id, PreboardingStatuses.Cancelled, reason, now, ct);
                continue;
            }
            var hire = (await hr.PreHireAsync(row.RecordId, ct))!;
            var token = RandomNumberGenerator.GetBytes(TokenBytes);
            var text = Convert.ToBase64String(token).TrimEnd('=').Replace('+', '-').Replace('/', '_');
            var outcome = await email.SendAsync(new ExternalEmailRequest(hire.PersonalEmail, InviteTemplate, $"/preboarding/{text}",
                new Dictionary<string, string> { ["startDate"] = row.StartDate.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture) }), ct);
            switch (outcome)
            {
                case ExternalEmailOutcomes.Sent:
                    var endOfStart = row.StartDate.AddDays(1).ToDateTime(TimeOnly.MinValue);
                    var startEnds = new DateTimeOffset(endOfStart, zone.GetUtcOffset(endOfStart)).ToUniversalTime();
                    var expires = now + LinkLifetime < startEnds ? now + LinkLifetime : startEnds;
                    await session.ExecAsync(
                        """
                        update hr.preboarding set status = 'sent', token_hash = @hash, expires_at = @expires, sent_at = @now, attempts = attempts + 1, updated_at = @now
                        where id = @id
                        """, new { id = row.Id, hash = SHA256.HashData(token), expires, now }, ct);
                    // The audit names the HR record, never the address.
                    await audit.WriteAsync(new AuditEntry(AuditActions.HrPreboardingSent, "preboarding", row.Id.ToString(),
                        Changes: new { recordId = row.RecordId, startDate = row.StartDate, expiresAt = expires }), ct);
                    sent++;
                    break;
                case ExternalEmailOutcomes.Bounced:
                    await EndAsync(row.Id, PreboardingStatuses.Failed, "bounced", now, ct);
                    break;
                default:
                    if (row.Attempts + 1 >= MaxAttempts)
                    {
                        await EndAsync(row.Id, PreboardingStatuses.Failed, "undeliverable", now, ct);
                    }
                    else
                    {
                        await session.ExecAsync("update hr.preboarding set attempts = attempts + 1, not_before = @next, updated_at = @now where id = @id",
                            new { id = row.Id, next = now + RetryEvery, now }, ct);
                    }
                    break;
            }
        }
        return sent;
    }

    private async Task<string?> CancelReasonAsync(Row row, DateOnly today, CancellationToken ct)
    {
        if (row.StartDate.DayNumber < today.DayNumber)
        {
            return "started";
        }
        if (await hr.PreHireAsync(row.RecordId, ct) is null)
        {
            return "not_pre_hire";
        }
        return await templates.RowAsync(row.TemplateId, ct) is { Active: true } template && template.Steps.Any(s => s.Type == JourneyStepTypes.Preboarding)
            ? null : "no_content";
    }

    private Task EndAsync(Guid id, string status, string reason, DateTimeOffset now, CancellationToken ct) =>
        session.ExecAsync("update hr.preboarding set status = @status, ended_reason = @reason, updated_at = @now where id = @id",
            new { id, status, reason, now }, ct);

    // ---- the page ----

    /// <summary>
    /// The page behind a link, for anyone holding it, on the tenant's host. An unknown, ended or expired link, or one
    /// whose template no longer has pre-boarding steps, is not found, all alike.
    /// </summary>
    public async Task<PreboardingPageView> PageAsync(string token, CancellationToken ct)
    {
        if (!await entitlements.IsEnabledAsync(Capabilities.HrJourneys, ct) || Decode(token) is not { } bytes)
        {
            throw AppException.NotFound();
        }
        var now = clock.UtcNow;
        var row = await session.QuerySingleOrDefaultAsync(
            "select id, template_id, start_date, expires_at from hr.preboarding where token_hash = @hash and status = 'sent' and expires_at > @now",
            new { hash = SHA256.HashData(bytes), now },
            r => (Id: r.GetGuid(0), Template: r.GetGuid(1), Start: DateOnly.FromDateTime(r.GetDateTime(2)), Expires: r.GetFieldValue<DateTimeOffset>(3)), ct);
        if (row == default || await templates.RowAsync(row.Template, ct) is not { Active: true } template)
        {
            throw AppException.NotFound();
        }
        var steps = template.Steps.Where(s => s.Type == JourneyStepTypes.Preboarding && s.Content is { Count: > 0 })
            .Select(s => new PreboardingStepView(s.Key, s.Names, s.Content!, row.Start.AddDays(s.OffsetDays))).ToList();
        if (steps.Count == 0)
        {
            throw AppException.NotFound();
        }
        await session.ExecAsync("update hr.preboarding set opened_at = coalesce(opened_at, @now) where id = @id", new { id = row.Id, now }, ct);
        return new PreboardingPageView(row.Start, row.Expires, steps);
    }

    private static byte[]? Decode(string? token)
    {
        if (token is not { Length: 43 } || token.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '-' or '_')))
        {
            return null;
        }
        try
        {
            return Convert.FromBase64String(token.Replace('-', '+').Replace('_', '/') + "=");
        }
        catch (FormatException)
        {
            return null;
        }
    }
}
