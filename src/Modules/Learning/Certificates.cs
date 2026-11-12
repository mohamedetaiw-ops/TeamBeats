using System.Globalization;
using System.Net;
using System.Text;
using TeamBeats.Authorization.Contracts;
using TeamBeats.Entitlements.Contracts;
using TeamBeats.Localization;
using TeamBeats.Modules.Identity.Contracts;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Primitives;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.Learning;

public sealed record CertificatePerson(Guid Id, string DisplayName);

public sealed record CertificateView(
    Guid Id,
    string Code,
    Guid CourseId,
    IReadOnlyDictionary<string, string> CourseTitles,
    int Version,
    CertificatePerson Learner,
    DateTimeOffset IssuedAt,
    DateTimeOffset? ExpiresAt);

/// <summary>
/// Certificates (spec 028 FR-010). Each completion has one, with an ID of twelve characters from an alphabet without
/// look-alikes. The holder sees and downloads their own; people with <c>learning.report</c> covering one of the
/// holder's org units (or the tenant) look one up by its ID. Lookups never leave the tenant (section 7). The PDF is
/// printed when it is downloaded, in the holder's language when the course has it, with the tenant's name.
/// </summary>
public sealed class Certificates(
    IDbSession session,
    ITenantContext context,
    IAuthorizationService authz,
    IEntitlementService entitlements,
    IOrganizationHierarchy hierarchy,
    IUserDirectory users,
    ITenantSettingsReader tenantSettings,
    IDocumentConverter converter,
    ILocalizer localizer)
{
    private const string CodeAlphabet = "ABCDEFGHJKMNPQRSTVWXYZ23456789";

    /// <summary>A new certificate ID in groups of four, for example <c>K7QM-2XRT-HP4N</c>.</summary>
    public static string NewCode()
    {
        var chars = new StringBuilder(14);
        for (var i = 0; i < 12; i++)
        {
            if (i is 4 or 8)
            {
                chars.Append('-');
            }
            chars.Append(CodeAlphabet[System.Security.Cryptography.RandomNumberGenerator.GetInt32(CodeAlphabet.Length)]);
        }
        return chars.ToString();
    }

    /// <summary>The ID as stored, from what was typed: upper case, grouped, or null when it cannot be one.</summary>
    public static string? NormalizeCode(string? code)
    {
        var clean = new string((code ?? "").Where(c => c is not ('-' or ' ')).Select(char.ToUpperInvariant).ToArray());
        return clean.Length == 12 && clean.All(c => CodeAlphabet.Contains(c, StringComparison.Ordinal)) ? $"{clean[..4]}-{clean[4..8]}-{clean[8..]}" : null;
    }

    private sealed record Row(Guid Id, Guid CourseId, Guid LearnerId, int Version, string Code, DateTimeOffset IssuedAt, DateTimeOffset? ExpiresAt, string Definition);

    private const string Select =
        """
        select c.id, c.course_id, c.learner_id, c.version, c.code, c.issued_at, c.expires_at, v.definition::text
        from learning.certificate c join learning.course_version v on v.course_id = c.course_id and v.version = c.version
        """;

    private static Row Read(Npgsql.NpgsqlDataReader r) => new(r.GetGuid(0), r.GetGuid(1), r.GetGuid(2), r.GetInt32(3), r.GetString(4), r.GetFieldValue<DateTimeOffset>(5),
        r.Get<DateTimeOffset?>("expires_at"), r.GetString(7));

    private async Task<Row> VisibleAsync(Row? row, CancellationToken ct)
    {
        if (row is null)
        {
            throw AppException.NotFound();
        }
        var user = context.RequireUser();
        if (row.LearnerId == user || (await authz.CheckAsync(Permissions.LearningReport, AuthzTarget.Tenant, ct)).Allowed)
        {
            return row;
        }
        foreach (var membership in await hierarchy.GetActiveMembershipsAsync(row.LearnerId, ct))
        {
            if ((await authz.CheckAsync(Permissions.LearningReport, AuthzTarget.OrgUnit(membership.OrgUnitId), ct)).Allowed)
            {
                return row;
            }
        }
        throw AppException.NotFound();
    }

    private async Task<CertificateView> ViewAsync(Row row, CancellationToken ct)
    {
        var learner = await users.GetAsync(row.LearnerId, ct);
        var definition = LearningJson.Read<CourseDefinition>(row.Definition);
        return new CertificateView(row.Id, row.Code, row.CourseId, definition.Titles, row.Version, new CertificatePerson(row.LearnerId, learner?.DisplayName ?? ""),
            row.IssuedAt, row.ExpiresAt);
    }

    public async Task<CertificateView> GetAsync(Guid id, CancellationToken ct)
    {
        await entitlements.DemandAsync(Capabilities.LearningCourses, ct);
        var row = await session.QuerySingleOrDefaultAsync($"{Select} where c.id = @id", new { id }, Read, ct);
        return await ViewAsync(await VisibleAsync(row, ct), ct);
    }

    /// <summary>Looks a certificate up by its ID (FR-010): the holder or someone who reports on them; 404 for anything else.</summary>
    public async Task<CertificateView> VerifyAsync(string? code, CancellationToken ct)
    {
        await entitlements.DemandAsync(Capabilities.LearningCourses, ct);
        if (NormalizeCode(code) is not { } normalized)
        {
            throw AppException.NotFound();
        }
        var row = await session.QuerySingleOrDefaultAsync($"{Select} where c.code = @normalized", new { normalized }, Read, ct);
        return await ViewAsync(await VisibleAsync(row, ct), ct);
    }

    public async Task<(Stream Content, string FileName)> PdfAsync(Guid id, CancellationToken ct)
    {
        var view = await GetAsync(id, ct);
        var locales = await tenantSettings.GetLocaleSettingsAsync(ct);
        var preferred = (await users.GetAsync(view.Learner.Id, ct))?.PreferredLocale;
        var language = new[] { preferred, locales.DefaultLocale }.FirstOrDefault(l => l is not null && view.CourseTitles.ContainsKey(l)) ?? view.CourseTitles.Keys.Order().First();
        var company = (await tenantSettings.GetBrandingAsync(ct)).DisplayName;
        var html = Html(language, company, view, localizer);
        var pdf = await converter.HtmlToPdfAsync(html, ct) ?? throw AppException.Conflict("conflict.learning.certificate_unavailable");
        return (pdf, $"certificate-{view.Code}.pdf");
    }

    /// <summary>The printable page. Every value is HTML encoded and nothing is loaded from outside; Arabic runs right to left.</summary>
    internal static string Html(string language, string company, CertificateView view, ILocalizer localizer)
    {
        static string E(string s) => WebUtility.HtmlEncode(s);
        var rtl = Locales.IsRightToLeft(language);
        var date = view.IssuedAt.ToString("d MMMM yyyy", CultureInfo.GetCultureInfo(language == "ar" ? "ar-AE" : "en-GB"));
        var html = new StringBuilder();
        html.Append(CultureInfo.InvariantCulture, $"<!doctype html><html lang=\"{language}\" dir=\"{(rtl ? "rtl" : "ltr")}\"><head><meta charset=\"utf-8\">");
        html.Append("<style>body{font-family:'Noto Naskh Arabic','Noto Sans','DejaVu Sans',sans-serif;color:#111;margin:0;text-align:center}")
            .Append("main{border:3px double #444;padding:48pt 36pt;margin:24pt}h1{font-size:26pt;margin:0 0 24pt}.company{font-size:14pt;margin-bottom:36pt}")
            .Append(".name{font-size:22pt;font-weight:700;margin:12pt 0}.course{font-size:16pt;margin:12pt 0 36pt}footer{font-size:10pt;color:#333}")
            .Append("bdi{unicode-bidi:isolate}</style></head><body><main>");
        html.Append(CultureInfo.InvariantCulture, $"<div class=\"company\">{E(company)}</div>");
        html.Append(CultureInfo.InvariantCulture, $"<h1>{E(localizer.Get("learning.certificate.pdf.title", language))}</h1>");
        html.Append(CultureInfo.InvariantCulture, $"<p>{E(localizer.Get("learning.certificate.pdf.awarded", language))}</p>");
        html.Append(CultureInfo.InvariantCulture, $"<p class=\"name\"><bdi>{E(view.Learner.DisplayName)}</bdi></p>");
        html.Append(CultureInfo.InvariantCulture, $"<p>{E(localizer.Get("learning.certificate.pdf.completed", language))}</p>");
        html.Append(CultureInfo.InvariantCulture, $"<p class=\"course\">{E(view.CourseTitles[language])}</p>");
        html.Append("<footer>");
        html.Append(CultureInfo.InvariantCulture, $"<p>{E(localizer.Get("learning.certificate.pdf.date", language))}: {E(date)}</p>");
        html.Append(CultureInfo.InvariantCulture, $"<p>{E(localizer.Get("learning.certificate.pdf.code", language))}: <bdi dir=\"ltr\">{E(view.Code)}</bdi></p>");
        html.Append("</footer></main></body></html>");
        return html.ToString();
    }
}
