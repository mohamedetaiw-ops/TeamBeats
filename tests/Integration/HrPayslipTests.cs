using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;
using TeamBeats.Audit.Contracts;
using TeamBeats.TenantContext;
using TeamBeats.Testing;
using Xunit;

namespace TeamBeats.Integration.Tests;

/// <summary>
/// Spec 019 T011 (US6, FR-010 to FR-012): a person reads their own payslips live from payroll, newest first, and only
/// their own: there is no way to name someone else, so a manager or an administrator gets their own or none. Both
/// endpoints need step-up and are never cached. A download is audited and carries a watermark with the person and the
/// time. When payroll does not answer nothing is shown. Without the plan feature the endpoints refuse.
/// </summary>
public sealed partial class HrSyncTests
{
    private const string Payslips = "/api/v1/me/payslips";

    private static Dictionary<string, object?> Slip(string month, string net, string? gross = null, string? deducted = null) =>
        new() { ["month"] = month, ["net"] = net, ["gross"] = gross, ["deducted"] = deducted, ["cur"] = "SAR" };

    private static byte[] SamplePdf()
    {
        using var document = new PdfDocument();
        document.AddPage();
        using var output = new MemoryStream();
        document.Save(output);
        return output.ToArray();
    }

    private static string PageText(byte[] pdf)
    {
        using var document = PdfReader.Open(new MemoryStream(pdf), PdfDocumentOpenMode.Import);
        var text = new StringBuilder();
        foreach (var page in document.Pages)
        {
            for (var i = 0; i < page.Contents.Elements.Count; i++)
            {
                text.Append(Encoding.ASCII.GetString(page.Contents.Elements.GetDictionary(i)!.Stream.UnfilteredValue));
            }
        }
        return text.ToString();
    }

    [Fact]
    public async Task People_read_only_their_own_payslips_live_with_step_up_and_a_watermarked_audited_pdf()
    {
        var t = await TenantAsync(people: ["sara", "omar"]);
        var (_, hr) = await RestConnectionAsync(t, payslips: true);
        hr.Employees = [Person("R1", t["sara"].Email, "E-100"), Person("R2", t["omar"].Email, "E-200", manager: null)];
        hr.Employees[0]["manager"] = "E-200";
        hr.Payslips["R1"] = [Slip("2026-09", "10250.50", "12000", "1749.50"), Slip("2026-10", "10500", "12000", "1500")];
        hr.Pdf = SamplePdf();
        await SyncAsync(t);

        // Scenario 1: newest period first, with net pay, and no copy kept by the browser.
        Assert.True((await (await t["sara"].Client.GetAsync($"{Payslips}-available")).ReadAsync<JsonElement>()).GetProperty("available").GetBoolean());
        var response = await t["sara"].Client.GetAsync(Payslips);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("no-store", response.Headers.CacheControl!.ToString(), StringComparison.Ordinal);
        var list = await response.ReadAsync<JsonElement>();
        Assert.Equal(("ok", true), (list.GetProperty("state").GetString(), list.GetProperty("hasPdf").GetBoolean()));
        var slips = list.GetProperty("payslips").EnumerateArray().ToArray();
        Assert.Equal(["2026-10", "2026-09"], slips.Select(s => s.GetProperty("period").GetString()!).ToArray());
        Assert.Equal((10500m, 1500m, "SAR"), (slips[0].GetProperty("net").GetDecimal(), slips[0].GetProperty("deductions").GetDecimal(), slips[0].GetProperty("currency").GetString()));
        await world.Database.AssertAuditedAsync(t.Id, AuditActions.HrPayslipViewed, t["sara"].Id.ToString());

        // Scenario 3: the PDF is audited with its period and carries who downloaded it and when.
        var pdf = await t["sara"].Client.GetAsync($"{Payslips}/2026-10/pdf");
        Assert.Equal(HttpStatusCode.OK, pdf.StatusCode);
        Assert.Equal("application/pdf", pdf.Content.Headers.ContentType!.MediaType);
        Assert.Contains("no-store", pdf.Headers.CacheControl!.ToString(), StringComparison.Ordinal);
        var stamped = PageText(await pdf.Content.ReadAsByteArrayAsync());
        Assert.Contains(t["sara"].Email, stamped, StringComparison.Ordinal);
        Assert.Contains(DateTimeOffset.UtcNow.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture), stamped, StringComparison.Ordinal);
        await world.Database.AssertAuditedAsync(t.Id, AuditActions.HrPayslipDownloaded, t["sara"].Id.ToString());
        Assert.Equal(HttpStatusCode.Conflict, (await t["sara"].Client.GetAsync($"{Payslips}/2026-08/pdf")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await t["sara"].Client.GetAsync($"{Payslips}/2026-13/pdf")).StatusCode);

        // Scenario 2: her manager and the administrator read their own records, never hers.
        hr.Asked.Clear();
        var managers = await (await t["omar"].Client.GetAsync($"{Payslips}?userId={t["sara"].Id}")).ReadAsync<JsonElement>();
        Assert.Equal(("ok", 0), (managers.GetProperty("state").GetString(), managers.GetProperty("payslips").GetArrayLength()));
        Assert.Equal(HttpStatusCode.Conflict, (await t["omar"].Client.GetAsync($"{Payslips}/2026-10/pdf")).StatusCode);
        Assert.DoesNotContain(hr.Asked, a => a.Contains("/R1/", StringComparison.Ordinal));
        Assert.Equal("not_matched", (await (await t["admin"].Client.GetAsync(Payslips)).ReadAsync<JsonElement>()).GetProperty("state").GetString());
        Assert.False((await (await t["admin"].Client.GetAsync($"{Payslips}-available")).ReadAsync<JsonElement>()).GetProperty("available").GetBoolean());

        // Scenario 4: payroll down means nothing is shown, not the list from before.
        hr.Down = true;
        var down = await (await t["sara"].Client.GetAsync(Payslips)).ReadAsync<JsonElement>();
        Assert.Equal(("unavailable", 0), (down.GetProperty("state").GetString(), down.GetProperty("payslips").GetArrayLength()));
        Assert.Equal(HttpStatusCode.Conflict, (await t["sara"].Client.GetAsync($"{Payslips}/2026-10/pdf")).StatusCode);
        hr.Down = false;

        // Both payslip endpoints need a recent sign-in (spec 026); the availability check shows nothing and does not.
        var endpoints = world.Factory.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>()
            .Where(e => e.RoutePattern.RawText?.StartsWith(Payslips, StringComparison.Ordinal) == true).ToList();
        Assert.Equal(3, endpoints.Count);
        Assert.All(endpoints.Where(e => !e.RoutePattern.RawText!.EndsWith("-available", StringComparison.Ordinal)),
            e => Assert.NotNull(e.Metadata.GetMetadata<StepUpMetadata>()));

        // Without hr.payslips in the plan there are no payslips.
        var plain = await TenantAsync("workplace", "sara");
        Assert.Equal(HttpStatusCode.Forbidden, (await plain["sara"].Client.GetAsync(Payslips)).StatusCode);
        Assert.False((await (await plain["sara"].Client.GetAsync($"{Payslips}-available")).ReadAsync<JsonElement>()).GetProperty("available").GetBoolean());
    }

    [Fact]
    public void The_watermark_is_plain_text_in_a_standard_font_and_a_broken_pdf_is_not_handed_out()
    {
        Assert.Equal(@"(a\(b\)c\\ ? x)", TeamBeats.Modules.Integrations.Hr.PdfWatermark.Literal("a(b)c\\ م x"));
        Assert.Null(TeamBeats.Modules.Integrations.Hr.PdfWatermark.Apply("%PDF-1.7 not really"u8.ToArray(), "x"));
        Assert.Contains("(someone@example.test", PageText(TeamBeats.Modules.Integrations.Hr.PdfWatermark.Apply(SamplePdf(), "someone@example.test")!), StringComparison.Ordinal);
    }
}
