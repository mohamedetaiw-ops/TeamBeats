using System.Globalization;
using System.Net;
using System.Text;
using Npgsql;
using TeamBeats.Audit.Contracts;
using TeamBeats.Authorization.Contracts;
using TeamBeats.Entitlements.Contracts;
using TeamBeats.Localization;
using TeamBeats.Messaging;
using TeamBeats.Modules.Identity.Contracts;
using TeamBeats.Modules.Notifications.Contracts;
using TeamBeats.Modules.Spaces.Contracts;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Primitives;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.Policies;

public sealed record EvidencePackRequest(IReadOnlyList<Guid>? PolicyIds, DateOnly? From, DateOnly? To);

/// <summary>Shares a ready pack read-only with a space the requester owns until the end of <paramref name="Until"/>; a null space stops sharing.</summary>
public sealed record EvidenceShareRequest(Guid? SpaceId, DateOnly? Until);

public sealed record EvidencePolicyRef(Guid Id, string Code, IReadOnlyDictionary<string, string> Names);

public sealed record EvidenceSpace(Guid Id, string Name);

public static class EvidenceStatuses
{
    public const string Queued = "queued";
    public const string Ready = "ready";
    public const string Failed = "failed";
}

public sealed record EvidencePackView(
    Guid Id,
    string Status,
    IReadOnlyList<EvidencePolicyRef> Policies,
    DateOnly From,
    DateOnly To,
    string Locale,
    PolicyPerson RequestedBy,
    DateTimeOffset RequestedAt,
    DateTimeOffset? CompletedAt,
    bool Pdf,
    bool Csv,
    EvidenceSpace? SharedSpace,
    DateTimeOffset? SharedUntil,
    bool CanShare);

/// <summary>Asks the background to build one evidence pack.</summary>
public sealed record EvidencePackRequested(Guid PackId) : IIntegrationEvent;

internal sealed record PackRow(Guid Id, Guid[] PolicyIds, DateOnly From, DateOnly To, string Locale, string Status, Guid RequestedBy, DateTimeOffset RequestedAt,
    DateTimeOffset? CompletedAt, string? PdfKey, string? CsvKey, Guid? SharedSpaceId, DateTimeOffset? SharedUntil)
{
    public const string Columns = "id, policy_ids, from_on, to_on, locale, status, requested_by, requested_at, completed_at, pdf_key, csv_key, shared_space_id, shared_until";

    public static PackRow Read(NpgsqlDataReader r) => new(r.GetGuid(0), r.GetFieldValue<Guid[]>(1), r.GetFieldValue<DateOnly>(2), r.GetFieldValue<DateOnly>(3), r.GetString(4),
        r.GetString(5), r.GetGuid(6), r.GetFieldValue<DateTimeOffset>(7), r.Get<DateTimeOffset?>("completed_at"), r.Get<string>("pdf_key"), r.Get<string>("csv_key"),
        r.Get<Guid?>("shared_space_id"), r.Get<DateTimeOffset?>("shared_until"));
}

/// <summary>
/// Evidence packs for auditors (spec 025 US8 scenarios 1 and 2, FR-016). A compliance officer picks policies they
/// manage and a date range; the pack is built in the background and holds, per policy, the versions in force in the
/// range with their effective dates and approval trail, the review history, attestation completion by unit for each
/// campaign and the list of attestations, the open exemptions and the linked controls, as PDF (through the conversion
/// service) and CSV. Asking, sharing and every download are audited. The requester may share a ready pack read-only
/// with a space they own, such as an auditor guest space, until an expiry date; the space's readers then open it.
/// Needs <c>policy.evidence</c>.
/// </summary>
public sealed class PolicyEvidence(
    IDbSession session,
    ITenantContext context,
    PolicyRegister register,
    PolicyControls controls,
    IEntitlementService entitlements,
    IUserDirectory users,
    IOrganizationHierarchy hierarchy,
    ITenantSettingsReader tenantSettings,
    ISpaceAccess spaces,
    IDocumentConverter converter,
    IObjectStorage storage,
    ILocalizer localizer,
    INotificationSender notifications,
    IAuditWriter audit,
    IOutbox outbox,
    IClock clock) : IMessageHandler<EvidencePackRequested>
{
    public const int MaxPolicies = 20;
    public const int MaxRangeDays = 1830;
    public const int MaxShareDays = 365;
    public const int ListMax = 100;
    public const string ReadyTemplate = "policies.evidence_ready";

    public static string Link => "/policies/evidence";

    private string Key(Guid id, string format) => $"policies/evidence/{context.RequireTenant():N}/{id:N}.{format}";

    private async Task<PackRow?> RowAsync(Guid id, bool forUpdate, CancellationToken ct) =>
        await session.QuerySingleOrDefaultAsync($"select {PackRow.Columns} from policies.evidence_pack where id = @id{(forUpdate ? " for update" : string.Empty)}",
            new { id }, PackRow.Read, ct);

    private async Task<TimeZoneInfo> ZoneAsync(CancellationToken ct) =>
        TimeZoneInfo.FindSystemTimeZoneById((await tenantSettings.GetLocaleSettingsAsync(ct)).TimeZone);

    private static DateTimeOffset StartOf(DateOnly day, TimeZoneInfo zone) => new(TimeZoneInfo.ConvertTimeToUtc(day.ToDateTime(TimeOnly.MinValue), zone), TimeSpan.Zero);

    private async Task<EvidencePackView> ViewAsync(PackRow p, bool owner, CancellationToken ct)
    {
        var refs = new List<EvidencePolicyRef>();
        foreach (var id in p.PolicyIds)
        {
            if (await register.RowAsync(id, false, ct) is { } policy)
            {
                refs.Add(new EvidencePolicyRef(policy.Id, policy.Code, policy.Names));
            }
        }
        EvidenceSpace? space = null;
        if (p.SharedSpaceId is { } spaceId)
        {
            space = new EvidenceSpace(spaceId, (await spaces.ForViewerAsync(spaceId, ct))?.Name ?? string.Empty);
        }
        return new EvidencePackView(p.Id, p.Status, refs, p.From, p.To, p.Locale, await register.PersonAsync(p.RequestedBy, ct), p.RequestedAt, p.CompletedAt,
            p.PdfKey is not null, p.CsvKey is not null, owner ? space : null, owner ? p.SharedUntil : null, owner && p.Status == EvidenceStatuses.Ready);
    }

    /// <summary>The requester sees their own packs; a reader of the space a pack is shared with sees it until the share ends.</summary>
    private async Task<(PackRow Pack, bool Owner)> ReadableAsync(Guid id, CancellationToken ct)
    {
        await entitlements.DemandAsync(Capabilities.PolicyEvidence, ct);
        var user = context.RequireUser();
        var pack = await RowAsync(id, false, ct) ?? throw AppException.NotFound();
        if (pack.RequestedBy == user)
        {
            return (pack, true);
        }
        if (pack.SharedSpaceId is { } space && pack.SharedUntil > clock.UtcNow && pack.Status == EvidenceStatuses.Ready
            && await spaces.ForViewerAsync(space, ct) is { Reads: true })
        {
            return (pack, false);
        }
        throw AppException.NotFound();
    }

    /// <summary>Asks for a pack (US8 scenario 1): policies the requester manages, a date range, built in the background.</summary>
    public async Task<EvidencePackView> RequestAsync(EvidencePackRequest request, CancellationToken ct)
    {
        await entitlements.DemandAsync(Capabilities.PolicyEvidence, ct);
        var user = context.RequireUser();
        var errors = new ValidationErrors();
        var ids = request.PolicyIds?.Distinct().ToArray() ?? [];
        if (ids.Length is 0 or > MaxPolicies)
        {
            errors.Add("policyIds", "validation.policies.evidence_policies");
        }
        if (request.From is not { } from || request.To is not { } to || from > to || to.DayNumber - from.DayNumber > MaxRangeDays)
        {
            errors.Add("from", "validation.policies.evidence_range");
            from = to = default;
        }
        errors.ThrowIfAny();
        foreach (var id in ids)
        {
            var policy = await register.VisibleAsync(id, false, ct);
            if (!await register.ManagesAsync(policy, ct))
            {
                throw AppException.Denied();
            }
        }
        var locale = (await users.GetAsync(user, ct))?.PreferredLocale is { } preferred && Locales.Supported.Contains(preferred)
            ? preferred
            : (await tenantSettings.GetLocaleSettingsAsync(ct)).DefaultLocale;
        var packId = Ids.New();
        var now = clock.UtcNow;
        await session.ExecAsync(
            """
            insert into policies.evidence_pack (tenant_id, id, policy_ids, from_on, to_on, locale, status, requested_by, requested_at)
            values (platform.current_tenant_id(), @packId, @ids, @from, @to, @locale, 'queued', @user, @now)
            """, new { packId, ids, from, to, locale, user, now }, ct);
        await outbox.EnqueueAsync(new EvidencePackRequested(packId), ct);
        await audit.WriteAsync(new AuditEntry(AuditActions.PolicyEvidenceExported, "policy_evidence_pack", packId.ToString(),
            Changes: new { policies = ids, from, to, locale }), ct);
        return await ViewAsync((await RowAsync(packId, false, ct))!, true, ct);
    }

    /// <summary>The requester's packs, newest first; with a space, the packs shared with it that the reader may open.</summary>
    public async Task<IReadOnlyList<EvidencePackView>> ListAsync(Guid? spaceId, CancellationToken ct)
    {
        await entitlements.DemandAsync(Capabilities.PolicyEvidence, ct);
        var user = context.RequireUser();
        if (spaceId is { } space)
        {
            if (await spaces.ForViewerAsync(space, ct) is not { Reads: true })
            {
                throw AppException.NotFound();
            }
            var shared = await session.QueryAsync(
                $"select {PackRow.Columns} from policies.evidence_pack where shared_space_id = @space and shared_until > @now and status = 'ready' order by requested_at desc limit @max",
                new { space, now = clock.UtcNow, max = ListMax }, PackRow.Read, ct);
            var list = new List<EvidencePackView>();
            foreach (var p in shared)
            {
                list.Add(await ViewAsync(p, p.RequestedBy == user, ct));
            }
            return list;
        }
        var mine = await session.QueryAsync($"select {PackRow.Columns} from policies.evidence_pack where requested_by = @user order by requested_at desc limit @max",
            new { user, max = ListMax }, PackRow.Read, ct);
        var views = new List<EvidencePackView>();
        foreach (var p in mine)
        {
            views.Add(await ViewAsync(p, true, ct));
        }
        return views;
    }

    public async Task<EvidencePackView> GetAsync(Guid id, CancellationToken ct)
    {
        var (pack, owner) = await ReadableAsync(id, ct);
        return await ViewAsync(pack, owner, ct);
    }

    /// <summary>One file of a ready pack; every download is audited, with whether it came through a shared space.</summary>
    public async Task<(Stream Content, string ContentType, string FileName)> DownloadAsync(Guid id, string format, CancellationToken ct)
    {
        var (pack, owner) = await ReadableAsync(id, ct);
        var key = format switch { "pdf" => pack.PdfKey, "csv" => pack.CsvKey, _ => throw AppException.NotFound() };
        if (pack.Status != EvidenceStatuses.Ready || key is null || await storage.OpenReadAsync(key, ct) is not { } stream)
        {
            throw AppException.NotFound();
        }
        await audit.WriteAsync(new AuditEntry(AuditActions.PolicyEvidenceDownloaded, "policy_evidence_pack", id.ToString(),
            Changes: new { format, space = owner ? null : pack.SharedSpaceId }), ct);
        return (stream, format == "pdf" ? "application/pdf" : "text/csv", $"policy-evidence-{pack.From:yyyyMMdd}-{pack.To:yyyyMMdd}.{format}");
    }

    /// <summary>Shares a ready pack read-only with a space the requester owns until the end of a day (US8 scenario 2), or stops sharing it.</summary>
    public async Task<EvidencePackView> ShareAsync(Guid id, EvidenceShareRequest request, CancellationToken ct)
    {
        await entitlements.DemandAsync(Capabilities.PolicyEvidence, ct);
        var user = context.RequireUser();
        var pack = await RowAsync(id, forUpdate: true, ct);
        if (pack is null || pack.RequestedBy != user)
        {
            throw AppException.NotFound();
        }
        if (pack.Status != EvidenceStatuses.Ready)
        {
            throw AppException.Conflict("conflict.policies.evidence_not_ready");
        }
        DateTimeOffset? until = null;
        if (request.SpaceId is { } spaceId)
        {
            var errors = new ValidationErrors();
            var space = await spaces.ForViewerAsync(spaceId, ct);
            if (space is not { Owns: true, Active: true })
            {
                errors.Add("spaceId", "validation.policies.evidence_space");
            }
            var zone = await ZoneAsync(ct);
            var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(clock.UtcNow.UtcDateTime, zone));
            if (request.Until is not { } day || day < today || day > today.AddDays(MaxShareDays))
            {
                errors.Add("until", "validation.policies.evidence_until");
            }
            else
            {
                until = StartOf(day.AddDays(1), zone);
            }
            errors.ThrowIfAny();
        }
        await session.ExecAsync("update policies.evidence_pack set shared_space_id = @space, shared_until = @until where id = @id",
            new { id, space = request.SpaceId, until }, ct);
        await audit.WriteAsync(new AuditEntry(AuditActions.PolicyEvidenceShared, "policy_evidence_pack", id.ToString(),
            Changes: new { from = pack.SharedSpaceId, to = request.SpaceId, until }), ct);
        return await ViewAsync((await RowAsync(id, false, ct))!, true, ct);
    }

    // ---- Building the pack ----

    private sealed record UnitCount(string Unit, int Members, int Attested);

    private sealed record CampaignPart(CampaignRow Campaign, IReadOnlyList<UnitCount> ByUnit);

    private sealed record AttestationLine(string Person, string Unit, int Version, DateTimeOffset At, string Channel, string Source);

    private sealed record ExemptionLine(string Who, string Status, DateOnly EndsOn, string Reason);

    private sealed record PolicyPart(PolicyRow Policy, IReadOnlyList<VersionRow> Versions, IReadOnlyList<ReviewRow> Reviews, IReadOnlyList<CampaignPart> Campaigns,
        IReadOnlyList<AttestationLine> Attestations, IReadOnlyList<ExemptionLine> Exemptions, IReadOnlyList<MappedControl> Controls);

    /// <summary>Builds the pack in the background: everything is read in the pack's tenant, under row level security.</summary>
    public async Task HandleAsync(EvidencePackRequested message, CancellationToken ct)
    {
        if (await RowAsync(message.PackId, forUpdate: true, ct) is not { Status: EvidenceStatuses.Queued } pack)
        {
            return;
        }
        var zone = await ZoneAsync(ct);
        var start = StartOf(pack.From, zone);
        var end = StartOf(pack.To.AddDays(1), zone);
        var memberships = (await hierarchy.ListActiveMembershipsAsync(ct)).ToLookup(m => m.UserId);
        var unitIds = memberships.SelectMany(g => g).Select(m => m.OrgUnitId).Distinct().ToList();
        var unitNames = await hierarchy.GetNamesAsync(unitIds, ct);
        string UnitOf(Guid user)
        {
            var list = memberships[user].ToList();
            var unit = (list.FirstOrDefault(m => m.IsPrimary) ?? list.FirstOrDefault())?.OrgUnitId;
            return unit is { } u && unitNames.TryGetValue(u, out var names) ? names.GetValueOrDefault(pack.Locale) ?? names.Values.FirstOrDefault() ?? string.Empty : string.Empty;
        }

        var parts = new List<PolicyPart>();
        foreach (var policyId in pack.PolicyIds)
        {
            if (await register.RowAsync(policyId, false, ct) is not { } policy)
            {
                continue;
            }
            var versions = await session.QueryAsync(
                $"""
                select {VersionRow.Columns} from policies.policy_version
                where policy_id = @policyId and number is not null and effective_from is not null and effective_from < @end and (superseded_at is null or superseded_at >= @start)
                order by number
                """, new { policyId, start, end }, VersionRow.Read, ct);
            var reviews = await session.QueryAsync(
                $"""
                select {ReviewRow.Columns} from policies.policy_review
                where policy_id = @policyId and ((due_at >= @start and due_at < @end) or (completed_at >= @start and completed_at < @end))
                order by due_at, id
                """, new { policyId, start, end }, ReviewRow.Read, ct);
            var campaignRows = await session.QueryAsync(
                $"select {CampaignRow.Columns} from policies.campaign where policy_id = @policyId and launched_at < @end and (closed_at is null or closed_at >= @start) order by launched_at, id",
                new { policyId, start, end }, CampaignRow.Read, ct);
            var campaignParts = new List<CampaignPart>();
            foreach (var c in campaignRows)
            {
                var members = await session.QueryAsync("select user_id, status from policies.campaign_member where campaign_id = @id and status <> 'left_audience'",
                    new { id = c.Id }, r => (User: r.GetGuid(0), Status: r.GetString(1)), ct);
                var byUnit = members.GroupBy(m => UnitOf(m.User))
                    .Select(g => new UnitCount(g.Key, g.Count(), g.Count(m => m.Status == MemberStatuses.Attested)))
                    .OrderByDescending(u => u.Members).ThenBy(u => u.Unit, StringComparer.Ordinal).ToList();
                campaignParts.Add(new CampaignPart(c, byUnit));
            }
            var attested = await session.QueryAsync(
                "select user_id, version, attested_at, channel, source from policies.attestation where policy_id = @policyId and attested_at >= @start and attested_at < @end order by attested_at, user_id",
                new { policyId, start, end }, r => (User: r.GetGuid(0), Version: r.GetInt32(1), At: r.GetFieldValue<DateTimeOffset>(2), Channel: r.GetString(3), Source: r.GetString(4)), ct);
            var lines = new List<AttestationLine>();
            foreach (var a in attested)
            {
                lines.Add(new AttestationLine((await register.PersonAsync(a.User, ct)).DisplayName, UnitOf(a.User), a.Version, a.At, a.Channel, a.Source));
            }
            var open = await session.QueryAsync(
                "select scope, subject_user_id, org_unit_id, status, ends_on, reason from policies.exemption where policy_id = @policyId and status in ('pending', 'approved') order by ends_on, id",
                new { policyId }, r => (Scope: r.GetString(0), User: r.Get<Guid?>("subject_user_id"), Unit: r.Get<Guid?>("org_unit_id"), Status: r.GetString(3),
                    EndsOn: r.GetFieldValue<DateOnly>(4), Reason: r.GetString(5)), ct);
            var exemptionUnits = await hierarchy.GetNamesAsync(open.Select(e => e.Unit).OfType<Guid>(), ct);
            var exemptions = new List<ExemptionLine>();
            foreach (var e in open)
            {
                var who = e.User is { } u ? (await register.PersonAsync(u, ct)).DisplayName
                    : e.Unit is { } unit && exemptionUnits.TryGetValue(unit, out var n) ? n.GetValueOrDefault(pack.Locale) ?? n.Values.FirstOrDefault() ?? string.Empty : string.Empty;
                exemptions.Add(new ExemptionLine(who, e.Status, e.EndsOn, e.Reason));
            }
            parts.Add(new PolicyPart(policy, versions, reviews, campaignParts, lines, exemptions, await controls.OfAsync(policyId, ct)));
        }

        var people = new Dictionary<Guid, string>();
        foreach (var id in parts.SelectMany(p => p.Versions.SelectMany(v => v.Approvals.Select(a => a.UserId)).Concat(p.Reviews.Select(r => r.Reviewer).OfType<Guid>())).Distinct())
        {
            people[id] = (await register.PersonAsync(id, ct)).DisplayName;
        }

        var now = clock.UtcNow;
        var csvKey = Key(pack.Id, "csv");
        await using (var csv = new MemoryStream(Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(Csv(pack, parts, people))).ToArray()))
        {
            await storage.PutAsync(csvKey, csv, "text/csv", ct);
        }
        string? pdfKey = null;
        await using (var pdf = await converter.HtmlToPdfAsync(Html(pack, parts, people), ct))
        {
            if (pdf is not null)
            {
                pdfKey = Key(pack.Id, "pdf");
                await storage.PutAsync(pdfKey, pdf, "application/pdf", ct);
            }
        }
        await session.ExecAsync("update policies.evidence_pack set status = 'ready', completed_at = @now, pdf_key = @pdfKey, csv_key = @csvKey where id = @id",
            new { id = pack.Id, now, pdfKey, csvKey }, ct);
        await notifications.SendAsync(new NotificationRequest([pack.RequestedBy], NotificationCategories.DocumentUpdate, ReadyTemplate, Link,
            DedupeKey: $"policies.evidence:{pack.Id:N}:ready"), ct);
    }

    private string L(string key, string locale) => localizer.Get($"policies.evidence.pdf.{key}", locale);

    private static string Day(DateTimeOffset? at, string _) =>
        at is { } a ? a.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : string.Empty;

    private string Names(IReadOnlyDictionary<string, string> names, string locale) => names.GetValueOrDefault(locale) ?? names.Values.FirstOrDefault() ?? string.Empty;

    /// <summary>A spreadsheet-safe cell: quoted, and text that a spreadsheet would read as a formula starts with an apostrophe.</summary>
    public static string Cell(string value)
    {
        var safe = value.Length > 0 && "=+-@\t\r".Contains(value[0]) ? "'" + value : value;
        return "\"" + safe.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
    }

    /// <summary>One row per fact: section, policy code, version, date, person or unit, and the detail.</summary>
    private string Csv(PackRow pack, IReadOnlyList<PolicyPart> parts, IReadOnlyDictionary<Guid, string> people)
    {
        var l = pack.Locale;
        var sb = new StringBuilder();
        void Row(params string[] cells) => sb.Append(string.Join(',', cells.Select(Cell))).Append("\r\n");
        Row(L("csv.section", l), L("csv.policy", l), L("csv.version", l), L("csv.date", l), L("csv.who", l), L("csv.unit", l), L("csv.detail", l));
        foreach (var p in parts)
        {
            var code = p.Policy.Code;
            foreach (var v in p.Versions)
            {
                Row(L("versions", l), code, v.Number?.ToString(CultureInfo.InvariantCulture) ?? "", Day(v.EffectiveFrom, l), "", "",
                    v.SupersededAt is { } s ? $"{L("supersededOn", l)} {Day(s, l)}" : L("inForce", l));
                foreach (var a in v.Approvals)
                {
                    Row(L("approvals", l), code, v.Number?.ToString(CultureInfo.InvariantCulture) ?? "", Day(a.At, l), Person(a.UserId), "", a.StepNames is { } n ? Names(n, l) : a.Step);
                }
            }
            foreach (var r in p.Reviews)
            {
                Row(L("reviews", l), code, r.Version.ToString(CultureInfo.InvariantCulture), Day(r.DueAt, l), r.Reviewer is { } u ? Person(u) : "", "",
                    r.Outcome is { } o ? localizer.Get($"policies.evidence.pdf.outcome.{o}", l) : L("noOutcome", l));
            }
            foreach (var c in p.Campaigns)
            {
                foreach (var u in c.ByUnit)
                {
                    Row(L("completion", l), code, c.Campaign.Version.ToString(CultureInfo.InvariantCulture), Day(c.Campaign.LaunchedAt, l), "", u.Unit,
                        $"{u.Attested}/{u.Members}");
                }
            }
            foreach (var a in p.Attestations)
            {
                Row(L("attestations", l), code, a.Version.ToString(CultureInfo.InvariantCulture), Day(a.At, l), a.Person, a.Unit, $"{a.Channel} {a.Source}");
            }
            foreach (var e in p.Exemptions)
            {
                Row(L("exemptions", l), code, "", e.EndsOn.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), e.Who, "", $"{localizer.Get($"policies.evidence.pdf.exemption.{e.Status}", l)}: {e.Reason}");
            }
            foreach (var c in p.Controls)
            {
                Row(L("controls", l), code, "", "", "", "", $"{c.Framework} {c.Code} {(c.Titles is { } t ? Names(t, l) : string.Empty)}".Trim());
            }
        }
        return sb.ToString();

        string Person(Guid id) => people.GetValueOrDefault(id) ?? string.Empty;
    }

    /// <summary>The printable pack. Every value is HTML encoded and nothing loads from outside; Arabic runs right to left.</summary>
    private string Html(PackRow pack, IReadOnlyList<PolicyPart> parts, IReadOnlyDictionary<Guid, string> people)
    {
        static string E(string s) => WebUtility.HtmlEncode(s);
        var l = pack.Locale;
        var sb = new StringBuilder();
        sb.Append(CultureInfo.InvariantCulture, $"<!doctype html><html lang=\"{l}\" dir=\"{(Locales.IsRightToLeft(l) ? "rtl" : "ltr")}\"><head><meta charset=\"utf-8\">");
        sb.Append("<style>body{font-family:'Noto Naskh Arabic','Noto Sans','DejaVu Sans',sans-serif;color:#111;font-size:10pt}h1{font-size:18pt}h2{font-size:14pt;margin-top:18pt;")
            .Append("break-before:page}h3{font-size:11pt;margin:12pt 0 4pt}table{border-collapse:collapse;width:100%}th,td{border:1px solid #999;padding:3pt 5pt;text-align:start;")
            .Append("vertical-align:top}bdi{unicode-bidi:isolate}.meta{color:#333}</style></head><body>");
        sb.Append(CultureInfo.InvariantCulture, $"<h1>{E(L("title", l))}</h1><p class=\"meta\">{E(L("range", l))}: <bdi dir=\"ltr\">{pack.From:yyyy-MM-dd} – {pack.To:yyyy-MM-dd}</bdi></p>");
        void Table(string heading, string[] head, IEnumerable<string[]> rows)
        {
            sb.Append(CultureInfo.InvariantCulture, $"<h3>{E(heading)}</h3>");
            var list = rows.ToList();
            if (list.Count == 0)
            {
                sb.Append(CultureInfo.InvariantCulture, $"<p class=\"meta\">{E(L("none", l))}</p>");
                return;
            }
            sb.Append("<table><thead><tr>");
            foreach (var h in head)
            {
                sb.Append(CultureInfo.InvariantCulture, $"<th scope=\"col\">{E(h)}</th>");
            }
            sb.Append("</tr></thead><tbody>");
            foreach (var row in list)
            {
                sb.Append("<tr>");
                foreach (var cell in row)
                {
                    sb.Append(CultureInfo.InvariantCulture, $"<td><bdi>{E(cell)}</bdi></td>");
                }
                sb.Append("</tr>");
            }
            sb.Append("</tbody></table>");
        }
        string Person(Guid id) => people.GetValueOrDefault(id) ?? string.Empty;
        foreach (var p in parts)
        {
            sb.Append(CultureInfo.InvariantCulture, $"<h2><bdi>{E(p.Policy.Code)}</bdi> <bdi>{E(Names(p.Policy.Names, l))}</bdi></h2>");
            Table(L("versions", l), [L("version", l), L("effective", l), L("superseded", l)],
                p.Versions.Select(v => new[] { v.Number?.ToString(CultureInfo.InvariantCulture) ?? "", Day(v.EffectiveFrom, l), Day(v.SupersededAt, l) }));
            Table(L("approvals", l), [L("version", l), L("step", l), L("approvedBy", l), L("date", l)],
                p.Versions.SelectMany(v => v.Approvals.Select(a => new[]
                {
                    v.Number?.ToString(CultureInfo.InvariantCulture) ?? "", a.StepNames is { } n ? Names(n, l) : a.Step, Person(a.UserId), Day(a.At, l),
                })));
            Table(L("reviews", l), [L("version", l), L("due", l), L("outcome", l), L("reviewer", l), L("completed", l)],
                p.Reviews.Select(r => new[]
                {
                    r.Version.ToString(CultureInfo.InvariantCulture), Day(r.DueAt, l),
                    r.Outcome is { } o ? localizer.Get($"policies.evidence.pdf.outcome.{o}", l) : L("noOutcome", l), r.Reviewer is { } u ? Person(u) : "", Day(r.CompletedAt, l),
                }));
            Table(L("completion", l), [L("version", l), L("launched", l), L("unit", l), L("attestedOf", l)],
                p.Campaigns.SelectMany(c => c.ByUnit.Select(u => new[]
                {
                    c.Campaign.Version.ToString(CultureInfo.InvariantCulture), Day(c.Campaign.LaunchedAt, l), u.Unit, $"{u.Attested} / {u.Members}",
                })));
            Table(L("attestations", l), [L("person", l), L("unit", l), L("version", l), L("date", l), L("channel", l)],
                p.Attestations.Select(a => new[] { a.Person, a.Unit, a.Version.ToString(CultureInfo.InvariantCulture), Day(a.At, l), $"{a.Channel} {a.Source}" }));
            Table(L("exemptions", l), [L("who", l), L("status", l), L("ends", l), L("reason", l)],
                p.Exemptions.Select(e => new[] { e.Who, localizer.Get($"policies.evidence.pdf.exemption.{e.Status}", l), e.EndsOn.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), e.Reason }));
            Table(L("controls", l), [L("framework", l), L("control", l)],
                p.Controls.Select(c => new[] { c.FrameworkNames is { } f ? Names(f, l) : c.Framework, $"{c.Code} {(c.Titles is { } t ? Names(t, l) : string.Empty)}".Trim() }));
        }
        sb.Append("</body></html>");
        return sb.ToString();
    }
}
