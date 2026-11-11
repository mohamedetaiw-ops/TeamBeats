using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using TeamBeats.Testing;
using Xunit;

namespace TeamBeats.Integration.Tests;

/// <summary>
/// Spec 019 T009 (US5, FR-009): a service form fills the requester's HR fields, a read-only HR value comes from the
/// server whatever the browser sends, a Confidential HR field fills only a Confidential form, a value that must be
/// current is read from HR again when the copy is older than 7 days, and when HR cannot answer the field says so and
/// the request cannot go in with a value the browser made up. Without the plan feature nothing comes from HR.
/// </summary>
public sealed partial class HrSyncTests
{
    private const string ServiceAdminPath = "/api/v1/services/admin";

    private static Dictionary<string, string> Labels(string en, string ar) => new() { ["en"] = en, ["ar"] = ar };

    private static object HrForm => new
    {
        fields = new object[]
        {
            new { key = "number", type = "text", labels = Labels("Employee number", "الرقم الوظيفي"), prefill = "hr.employee_number", readOnly = true },
            new { key = "title", type = "text", labels = Labels("Job title", "المسمى الوظيفي"), prefill = "hr.job_title", readOnly = true, mustBeCurrent = true },
            new { key = "grade", type = "text", labels = Labels("Grade", "الدرجة"), prefill = "hr.grade" },
            new { key = "cost", type = "text", labels = Labels("Cost centre", "مركز التكلفة"), prefill = "hr.cost_centre", readOnly = true },
            new { key = "site", type = "text", labels = Labels("Location", "الموقع"), prefill = "hr.location" },
            new { key = "hired", type = "date", labels = Labels("Hire date", "تاريخ التعيين"), prefill = "hr.hire_date" },
        },
    };

    private static async Task<HttpResponseMessage> CreateServiceAsync(TestTenant t, string classification, object form)
    {
        var admin = t["admin"].Client;
        var created = await admin.PostAsJsonAsync(ServiceAdminPath, new { kind = "form", names = Labels("Letter", "خطاب"), classification });
        Assert.True(created.StatusCode == HttpStatusCode.Created || created.StatusCode == HttpStatusCode.OK, await created.Content.ReadAsStringAsync());
        var id = (await created.ReadAsync<JsonElement>()).GetProperty("service").GetProperty("id").GetGuid();
        var draft = await admin.PutAsJsonAsync($"{ServiceAdminPath}/{id}/draft", new { form, allowOnBehalf = false, maxAttachments = 0 });
        if (!draft.IsSuccessStatusCode)
        {
            return draft;
        }
        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsJsonAsync($"{ServiceAdminPath}/{id}/publish", new { })).StatusCode);
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new { id }) };
        return response;
    }

    private static async Task<Guid> ServiceIdAsync(TestTenant t, string classification) =>
        (await (await CreateServiceAsync(t, classification, HrForm)).ReadAsync<JsonElement>()).GetProperty("id").GetGuid();

    private static async Task<JsonElement> PrefillAsync(TestTenant t, string user, Guid service) =>
        await (await t[user].Client.GetAsync($"/api/v1/services/{service}/prefill")).ReadAsync<JsonElement>();

    private static string[] Strings(JsonElement e, string name) => e.GetProperty(name).EnumerateArray().Select(v => v.GetString()!).ToArray();

    [Fact]
    public async Task Forms_fill_the_requesters_hr_fields_keep_read_only_values_and_confirm_stale_ones_with_hr()
    {
        var t = await TenantAsync(people: ["sara", "omar"]);
        var (_, hr) = await RestConnectionAsync(t);
        hr.Employees = [Person("R1", t["sara"].Email, "E-100"), Person("R2", t["omar"].Email, "E-200")];
        await SyncAsync(t);
        var internalForm = await ServiceIdAsync(t, "internal");
        var confidentialForm = await ServiceIdAsync(t, "confidential");

        // Scenario 1 and 4: HR values fill the form; grade and cost centre are Confidential and stay out of an Internal form.
        var prefill = await PrefillAsync(t, "sara", internalForm);
        var values = prefill.GetProperty("values");
        Assert.Equal(("E-100", "Accountant", "Riyadh HQ", "2024-03-01"),
            (values.GetProperty("number").GetString(), values.GetProperty("title").GetString(), values.GetProperty("site").GetString(), values.GetProperty("hired").GetString()));
        Assert.False(values.TryGetProperty("grade", out _));
        Assert.False(values.TryGetProperty("cost", out _));
        Assert.Equal(["cost", "number", "title"], Strings(prefill, "readOnly").Order().ToArray());
        Assert.Empty(Strings(prefill, "unconfirmed"));
        var confidential = (await PrefillAsync(t, "sara", confidentialForm)).GetProperty("values");
        Assert.Equal(("G7", "CC-100"), (confidential.GetProperty("grade").GetString(), confidential.GetProperty("cost").GetString()));

        // Scenario 2: a read-only value comes from HR, not from the browser; an editable one keeps what the person typed.
        var submitted = await t["sara"].Client.PostAsJsonAsync("/api/v1/requests",
            new { serviceId = confidentialForm, data = new { number = "E-999", cost = "CC-1", grade = "G9", site = "Jeddah" } });
        Assert.True(submitted.StatusCode == HttpStatusCode.Created, await submitted.Content.ReadAsStringAsync());
        var data = (await submitted.ReadAsync<JsonElement>()).GetProperty("data");
        Assert.Equal(("E-100", "CC-100", "G9", "Jeddah"),
            (data.GetProperty("number").GetString(), data.GetProperty("cost").GetString(), data.GetProperty("grade").GetString(), data.GetProperty("site").GetString()));

        // Scenario 3: the copy is older than 7 days, so a field that must be current is read from HR now.
        Assert.Equal(1L, await world.Database.TenantScalarAsync<long>(t.Id,
            "with c as (update integrations.hr_employee set last_seen_at = now() - interval '8 days' where record_id = 'R1' returning 1) select count(*) from c"));
        hr.Employees[0]["job"] = new Dictionary<string, object?> { ["title"] = "Senior accountant", ["grade"] = "G7" };
        var live = (await PrefillAsync(t, "sara", internalForm)).GetProperty("values");
        Assert.Equal(("Senior accountant", "E-100"), (live.GetProperty("title").GetString(), live.GetProperty("number").GetString()));

        // When HR does not answer, the field says it could not be confirmed and the request cannot take the browser's value.
        hr.Down = true;
        var down = await PrefillAsync(t, "sara", internalForm);
        Assert.Equal(["title"], Strings(down, "unconfirmed"));
        Assert.False(down.GetProperty("values").TryGetProperty("title", out _));
        var refused = await t["sara"].Client.PostAsJsonAsync("/api/v1/requests", new { serviceId = internalForm, data = new { title = "Director" } });
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Contains("validation.services.prefill_unconfirmed", await refused.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        hr.Down = false;

        // Only HR fields can be read-only or need a current value.
        var wrong = await CreateServiceAsync(t, "internal", new
        {
            fields = new object[] { new { key = "email", type = "text", labels = Labels("Email", "البريد"), prefill = "profile.email", mustBeCurrent = true } },
        });
        Assert.Equal(HttpStatusCode.BadRequest, wrong.StatusCode);
        Assert.Contains("validation.services.field_prefill_hr_only", await wrong.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        // Each person sees their own HR record, and only their own.
        Assert.Equal("E-200", (await PrefillAsync(t, "omar", internalForm)).GetProperty("values").GetProperty("number").GetString());

        // Without hr.form_prefill in the plan nothing comes from HR, and nothing from another tenant's HR either.
        var plain = await TenantAsync("workplace", "sara");
        var plainForm = await ServiceIdAsync(plain, "confidential");
        var none = await PrefillAsync(plain, "sara", plainForm);
        Assert.Empty(none.GetProperty("values").EnumerateObject());
        Assert.Equal(["cost", "number", "title"], Strings(none, "readOnly").Order().ToArray());
    }
}
