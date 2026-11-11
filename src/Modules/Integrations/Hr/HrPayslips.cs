using Microsoft.Extensions.Logging;
using TeamBeats.Audit.Contracts;
using TeamBeats.Entitlements.Contracts;
using TeamBeats.Modules.Identity.Contracts;
using TeamBeats.Primitives;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.Integrations.Hr;

/// <summary>One payslip as payroll reports it. Never stored (section 8).</summary>
public sealed record HrPayslip(string Period, decimal? Gross, decimal? Deductions, decimal Net, string? Currency);

/// <summary>
/// My payslips. <see cref="State"/> is <c>ok</c>, <c>not_matched</c> (no HR record matches you) or <c>unavailable</c>
/// (payroll did not answer, or the HR connection has no payslips). Nothing is shown when payroll is unavailable, never
/// an older list (FR-012).
/// </summary>
public sealed record PayslipList(string State, IReadOnlyList<HrPayslip> Payslips, bool HasPdf);

/// <summary>
/// Payslips (spec 019 US6, FR-010 to FR-012): only the signed-in person's own, read live from payroll through the HR
/// REST pack. There is no way to name another person, so managers, HR officers and administrators cannot read them in
/// TeamBeats. The endpoints need step-up. Views and downloads are audited with the period and never the amounts; the
/// PDF carries a watermark with the person and the time. Payslips never reach search, models or notification bodies:
/// nothing here is a search source, an agent tool, a connector action or an event.
/// </summary>
public sealed class HrPayslips(ITenantContext context, IEntitlementService entitlements, IAuditWriter audit, IClock clock, IUserDirectory users,
    HrRecords records, HrRestPack rest, ILogger<HrPayslips> log)
{
    public const string Ok = "ok";
    public const string NotMatched = "not_matched";
    public const string Unavailable = "unavailable";

    /// <summary>The plan includes payslips and an HR record matches the signed-in person.</summary>
    public async Task<bool> AvailableAsync(CancellationToken ct) =>
        await entitlements.IsEnabledAsync(Capabilities.HrPayslips, ct) && await records.LinkAsync(context.RequireUser(), ct) is { Connection: { } c }
        && HrRestPack.Parse(c.Settings)?.PayslipsPath is not null;

    public async Task<PayslipList> ListAsync(CancellationToken ct)
    {
        await entitlements.DemandAsync(Capabilities.HrPayslips, ct);
        var user = context.RequireUser();
        if (await records.LinkAsync(user, ct) is not { } link)
        {
            return new PayslipList(NotMatched, [], false);
        }
        if (link.Connection is not { } connection)
        {
            return new PayslipList(Unavailable, [], false);
        }
        IReadOnlyList<HrPayslip>? payslips;
        try
        {
            (payslips, var failure) = await rest.ReadPayslipsAsync(connection, link.Record, ct);
            if (failure is not null)
            {
                log.LogWarning("Payslips could not be read: {ErrorClass}", failure.ErrorClass);
            }
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            payslips = null;
        }
        if (payslips is null)
        {
            return new PayslipList(Unavailable, [], false);
        }
        await audit.WriteAsync(new AuditEntry(AuditActions.HrPayslipViewed, "user", user.ToString(), Changes: new { periods = payslips.Select(p => p.Period).ToArray() }), ct);
        return new PayslipList(Ok, payslips, RestPdf(connection));
    }

    private static bool RestPdf(Runtime.PackConnection connection) => HrRestPack.Parse(connection.Settings)?.PayslipPdfPath is not null;

    /// <summary>The PDF of one of my periods, with a watermark naming me and the time; null when payroll cannot give it.</summary>
    public async Task<byte[]?> PdfAsync(string period, CancellationToken ct)
    {
        await entitlements.DemandAsync(Capabilities.HrPayslips, ct);
        var user = context.RequireUser();
        if (!HrRestPack.PayslipPeriod().IsMatch(period))
        {
            throw AppException.Validation("period", "validation.hr.payslip_period");
        }
        if (await records.LinkAsync(user, ct) is not { Connection: { } connection } link)
        {
            return null;
        }
        byte[]? pdf;
        try
        {
            (pdf, _) = await rest.ReadPayslipPdfAsync(connection, link.Record, period, ct);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            pdf = null;
        }
        if (pdf is null)
        {
            return null;
        }
        var account = await users.GetAsync(user, ct);
        var now = clock.UtcNow;
        var marked = PdfWatermark.Apply(pdf, $"{account?.Email ?? user.ToString()}  {now:yyyy-MM-dd HH:mm} UTC");
        if (marked is null)
        {
            // A PDF that cannot carry the watermark is not handed out (FR-011).
            log.LogWarning("A payslip PDF could not be watermarked");
            return null;
        }
        await audit.WriteAsync(new AuditEntry(AuditActions.HrPayslipDownloaded, "user", user.ToString(), Changes: new { period }), ct);
        return marked;
    }
}
