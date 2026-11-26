using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using TeamBeats.Audit.Contracts;
using TeamBeats.Authorization.Contracts;
using TeamBeats.Entitlements.Contracts;
using TeamBeats.Localization;
using TeamBeats.Modules.Identity.Contracts;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Primitives;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.Documents;

public sealed record CertificateRequest(DateTimeOffset? From, DateTimeOffset? To);

public sealed record DispositionCertificateView(Guid Id, string Code, DateTimeOffset From, DateTimeOffset To, int Records, string Digest, Guid IssuedBy, string IssuerName,
    DateTimeOffset IssuedAt);

/// <summary>A certificate with the disposition records it lists (the first ones, when there are many) and whether they still match its digest.</summary>
public sealed record DispositionCertificateDetail(DispositionCertificateView Certificate, IReadOnlyList<DispositionRecordView> Items, bool Intact);

/// <summary>
/// Disposition certificates (spec 011 R3). A records manager issues one for a range of dispositions; it keeps the list of
/// disposition records it covers, so it never changes after it is issued, and a SHA-256 digest over them in a fixed
/// order. Verify by ID recomputes the digest. The PDF is printed when downloaded, in the reader's language, and lists
/// numbers, labels, versions, how and when each document was disposed of and who approved it; never names or content.
/// </summary>
public sealed class DispositionCertificates(
    IDbSession session,
    ITenantContext context,
    IAuthorizationService authz,
    IEntitlementService entitlements,
    IUserDirectory users,
    ITenantSettingsReader tenantSettings,
    IDocumentConverter converter,
    ILocalizer localizer,
    IAuditWriter audit,
    IClock clock)
{
    public const int MaxRangeDays = 366;
    public const int MaxItems = 10_000;
    public const int MaxShown = 500;
    public const int MaxListed = 200;

    private async Task RequireAsync(CancellationToken ct)
    {
        await entitlements.DemandAsync(Capabilities.RecordsRetention, ct);
        context.RequireUser();
        await authz.DemandAsync(Permissions.RecordsManage, AuthzTarget.Tenant, ct);
    }

    private sealed record Item(Guid Id, Guid DocumentId, string? Number, string LabelNames, int Versions, string Mode, Guid? ApprovedBy, DateTimeOffset DisposedAt);

    private static Item ReadItem(Npgsql.NpgsqlDataReader r) => new(r.GetGuid(0), r.GetGuid(1), r.Get<string>("number"), r.GetString(3), r.GetInt32(4), r.GetString(5),
        r.Get<Guid?>("approved_by"), r.GetFieldValue<DateTimeOffset>(7));

    private const string ItemColumns = "r.id, r.document_id, r.number, r.label_names::text, r.versions, r.mode, r.approved_by, r.disposed_at";

    /// <summary>The digest over the listed records: one line per record in list order, with fields that never change after disposal.</summary>
    internal static string Digest(IEnumerable<(Guid Id, Guid DocumentId, string? Number, int Versions, string Mode, Guid? ApprovedBy, DateTimeOffset DisposedAt)> items)
    {
        var text = new StringBuilder();
        foreach (var i in items)
        {
            text.Append(CultureInfo.InvariantCulture,
                $"{i.Id:D}|{i.DocumentId:D}|{i.Number}|{i.Versions}|{i.Mode}|{i.ApprovedBy?.ToString("D") ?? ""}|{i.DisposedAt.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.ffffffZ", CultureInfo.InvariantCulture)}\n");
        }
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())));
    }

    private static string Digest(IEnumerable<Item> items) => Digest(items.Select(i => (i.Id, i.DocumentId, i.Number, i.Versions, i.Mode, i.ApprovedBy, i.DisposedAt)));

    private static DispositionRecordView View(Item i) =>
        new(i.Id, i.DocumentId, i.Number, DocJson.Read<Dictionary<string, string>>(i.LabelNames), i.Versions, i.Mode, i.ApprovedBy, i.DisposedAt);

    private sealed record Row(Guid Id, string Code, DateTimeOffset From, DateTimeOffset To, int Records, string Digest, Guid IssuedBy, DateTimeOffset IssuedAt);

    private const string Select = "select id, code, from_at, to_at, records, digest, issued_by, issued_at from documents.disposition_certificate";

    private static Row Read(Npgsql.NpgsqlDataReader r) => new(r.GetGuid(0), r.GetString(1), r.GetFieldValue<DateTimeOffset>(2), r.GetFieldValue<DateTimeOffset>(3),
        r.GetInt32(4), r.GetString(5), r.GetGuid(6), r.GetFieldValue<DateTimeOffset>(7));

    private async Task<DispositionCertificateView> ViewAsync(Row row, CancellationToken ct) =>
        new(row.Id, row.Code, row.From, row.To, row.Records, row.Digest, row.IssuedBy, (await users.GetAsync(row.IssuedBy, ct))?.DisplayName ?? "", row.IssuedAt);

    private Task<List<Item>> ItemsAsync(Guid id, int? limit, CancellationToken ct) =>
        session.QueryAsync(
            $"""
            select {ItemColumns} from documents.disposition_certificate_item i join documents.disposition_record r on r.id = i.record_id
            where i.certificate_id = @id order by i.position{(limit is { } n ? $" limit {n}" : "")}
            """, new { id }, ReadItem, ct);

    public async Task<IReadOnlyList<DispositionCertificateView>> ListAsync(CancellationToken ct)
    {
        await RequireAsync(ct);
        var rows = await session.QueryAsync($"{Select} order by issued_at desc, id limit {MaxListed}", null, Read, ct);
        var list = new List<DispositionCertificateView>(rows.Count);
        foreach (var row in rows)
        {
            list.Add(await ViewAsync(row, ct));
        }
        return list;
    }

    /// <summary>
    /// Issues a certificate for the dispositions from <c>From</c> (inclusive) to <c>To</c> (exclusive). The range ends no
    /// later than now, spans at most a year and holds at least one and at most <see cref="MaxItems"/> disposition records.
    /// </summary>
    public async Task<DispositionCertificateDetail> IssueAsync(CertificateRequest request, CancellationToken ct)
    {
        await RequireAsync(ct);
        var now = clock.UtcNow;
        if (request.From is not { } from || request.To is not { } to || from >= to || to > now.AddMinutes(1) || to - from > TimeSpan.FromDays(MaxRangeDays))
        {
            throw AppException.Validation("to", "validation.records.certificate_range");
        }
        var items = await session.QueryAsync(
            $"select {ItemColumns} from documents.disposition_record r where r.disposed_at >= @from and r.disposed_at < @to order by r.disposed_at, r.id limit {MaxItems + 1}",
            new { from, to }, ReadItem, ct);
        if (items.Count == 0)
        {
            throw AppException.Validation("from", "validation.records.certificate_empty");
        }
        if (items.Count > MaxItems)
        {
            throw AppException.Validation("from", "validation.records.certificate_too_many");
        }
        var id = Ids.New();
        var code = ReadableCodes.New();
        var digest = Digest(items);
        var user = context.RequireUser();
        await session.ExecAsync(
            """
            insert into documents.disposition_certificate (tenant_id, id, code, from_at, to_at, records, digest, issued_by, issued_at)
            values (platform.current_tenant_id(), @id, @code, @from, @to, @count, @digest, @user, @now)
            """, new { id, code, from, to, count = items.Count, digest, user, now }, ct);
        await session.ExecAsync(
            """
            insert into documents.disposition_certificate_item (tenant_id, certificate_id, record_id, position)
            select platform.current_tenant_id(), @id, r, p::int from unnest(@records::uuid[]) with ordinality as t(r, p)
            """, new { id, records = items.Select(i => i.Id).ToArray() }, ct);
        await audit.WriteAsync(new AuditEntry(AuditActions.RecordsCertificateIssued, "disposition_certificate", id.ToString(),
            Changes: new { code, from, to, records = items.Count, digest }), ct);
        var row = new Row(id, code, from, to, items.Count, digest, user, now);
        return new DispositionCertificateDetail(await ViewAsync(row, ct), items.Take(MaxShown).Select(View).ToList(), true);
    }

    private async Task<DispositionCertificateDetail> DetailAsync(Row? row, CancellationToken ct)
    {
        if (row is null)
        {
            throw AppException.NotFound();
        }
        var items = await ItemsAsync(row.Id, null, ct);
        var intact = items.Count == row.Records && Digest(items) == row.Digest;
        return new DispositionCertificateDetail(await ViewAsync(row, ct), items.Take(MaxShown).Select(View).ToList(), intact);
    }

    public async Task<DispositionCertificateDetail> GetAsync(Guid id, CancellationToken ct)
    {
        await RequireAsync(ct);
        return await DetailAsync(await session.QuerySingleOrDefaultAsync($"{Select} where id = @id", new { id }, Read, ct), ct);
    }

    /// <summary>Looks a certificate up by its ID within the tenant and says whether its records still match the digest.</summary>
    public async Task<DispositionCertificateDetail> VerifyAsync(string? code, CancellationToken ct)
    {
        await RequireAsync(ct);
        if (ReadableCodes.Normalize(code) is not { } normalized)
        {
            throw AppException.NotFound();
        }
        return await DetailAsync(await session.QuerySingleOrDefaultAsync($"{Select} where code = @normalized", new { normalized }, Read, ct), ct);
    }

    public async Task<(Stream Content, string FileName)> PdfAsync(Guid id, CancellationToken ct)
    {
        await RequireAsync(ct);
        var row = await session.QuerySingleOrDefaultAsync($"{Select} where id = @id", new { id }, Read, ct) ?? throw AppException.NotFound();
        var items = await ItemsAsync(id, null, ct);
        var locales = await tenantSettings.GetLocaleSettingsAsync(ct);
        var preferred = (await users.GetAsync(context.RequireUser(), ct))?.PreferredLocale;
        var language = new[] { preferred, locales.DefaultLocale }.FirstOrDefault(l => l is "en" or "ar") ?? "en";
        var names = new Dictionary<Guid, string>();
        foreach (var approver in items.Select(i => i.ApprovedBy).Append(row.IssuedBy).OfType<Guid>().Distinct())
        {
            names[approver] = (await users.GetAsync(approver, ct))?.DisplayName ?? "";
        }
        var company = (await tenantSettings.GetBrandingAsync(ct)).DisplayName;
        var html = Html(language, company, row.Code, row.From, row.To, row.Digest, names[row.IssuedBy], row.IssuedAt,
            items.Select(i => (i.Number, Label: LabelIn(i.LabelNames, language), i.Versions, i.Mode, Approver: i.ApprovedBy is { } a ? names[a] : null, i.DisposedAt)).ToList(),
            localizer);
        var pdf = await converter.HtmlToPdfAsync(html, ct) ?? throw AppException.Conflict("conflict.records.certificate_unavailable");
        return (pdf, $"disposition-certificate-{row.Code}.pdf");
    }

    private static string LabelIn(string json, string language)
    {
        var names = DocJson.Read<Dictionary<string, string>>(json);
        return names.TryGetValue(language, out var name) ? name : names.TryGetValue("en", out var en) ? en : names.Values.FirstOrDefault() ?? "";
    }

    /// <summary>The printable page. Every value is HTML encoded and nothing is loaded from outside; Arabic runs right to left.</summary>
    internal static string Html(string language, string company, string code, DateTimeOffset from, DateTimeOffset to, string digest, string issuer, DateTimeOffset issuedAt,
        IReadOnlyList<(string? Number, string Label, int Versions, string Mode, string? Approver, DateTimeOffset DisposedAt)> items, ILocalizer localizer)
    {
        static string E(string s) => WebUtility.HtmlEncode(s);
        string T(string key) => E(localizer.Get($"records.certificate.pdf.{key}", language));
        var rtl = Locales.IsRightToLeft(language);
        var culture = CultureInfo.GetCultureInfo(language == "ar" ? "ar-AE" : "en-GB");
        string Day(DateTimeOffset d) => E(d.ToUniversalTime().ToString("d MMMM yyyy", culture));
        var html = new StringBuilder();
        html.Append(PdfPage.Head(language, rtl, $"{localizer.Get("records.certificate.pdf.title", language)} {code}"));
        html.Append("<style>body{font-family:'Noto Naskh Arabic','Noto Sans','DejaVu Sans',sans-serif;color:#111;margin:24pt;font-size:10pt}")
            .Append("h1{font-size:20pt;margin:0 0 12pt}.company{font-size:12pt;margin-bottom:18pt}dl{display:grid;grid-template-columns:max-content 1fr;gap:4pt 12pt;margin:0 0 18pt}")
            .Append("dt{font-weight:700}dd{margin:0}table{width:100%;border-collapse:collapse}th,td{border:1px solid #888;padding:4pt 6pt;text-align:start;vertical-align:top}")
            .Append("th{background:#eee}footer{margin-top:18pt;font-size:9pt;color:#333}bdi{unicode-bidi:isolate}code{font-family:'DejaVu Sans Mono',monospace;word-break:break-all}</style></head><body>");
        html.Append(CultureInfo.InvariantCulture, $"<div class=\"company\">{E(company)}</div><h1>{T("title")}</h1><p>{T("statement")}</p><dl>");
        html.Append(CultureInfo.InvariantCulture, $"<dt>{T("code")}</dt><dd><bdi dir=\"ltr\">{E(code)}</bdi></dd>");
        html.Append(CultureInfo.InvariantCulture, $"<dt>{T("range")}</dt><dd>{Day(from)} {T("until")} {Day(to.AddTicks(-1))}</dd>");
        html.Append(CultureInfo.InvariantCulture, $"<dt>{T("records")}</dt><dd>{items.Count.ToString(culture)}</dd>");
        html.Append(CultureInfo.InvariantCulture, $"<dt>{T("issuer")}</dt><dd><bdi>{E(issuer)}</bdi></dd><dt>{T("issued")}</dt><dd>{Day(issuedAt)}</dd></dl>");
        html.Append(CultureInfo.InvariantCulture,
            $"<table><thead><tr><th>{T("number")}</th><th>{T("label")}</th><th>{T("versions")}</th><th>{T("mode")}</th><th>{T("approvedBy")}</th><th>{T("disposed")}</th></tr></thead><tbody>");
        foreach (var i in items)
        {
            html.Append(CultureInfo.InvariantCulture,
                $"<tr><td><bdi dir=\"ltr\">{E(i.Number ?? "")}</bdi></td><td><bdi>{E(i.Label)}</bdi></td><td>{i.Versions.ToString(culture)}</td><td>{T($"modes.{i.Mode}")}</td>");
            html.Append(CultureInfo.InvariantCulture, $"<td><bdi>{E(i.Approver ?? "")}</bdi></td><td>{Day(i.DisposedAt)}</td></tr>");
        }
        html.Append(CultureInfo.InvariantCulture, $"</tbody></table><footer><p>{T("digest")}: <code dir=\"ltr\">{E(digest)}</code></p><p>{T("verify")}</p></footer></body></html>");
        return html.ToString();
    }
}
