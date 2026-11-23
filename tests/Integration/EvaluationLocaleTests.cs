using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using TeamBeats.Authorization.Contracts;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Testing;
using Xunit;

namespace TeamBeats.Integration.Tests;

/// <summary>
/// Spec 034 R2 T024 (US1 scenario 6): French, Urdu and Hindi cases are offered to tenants that enabled those locales,
/// in edits and imports alike; other tenants keep Arabic and English.
/// </summary>
[Collection(WorldCollection.Name)]
public sealed class EvaluationLocaleTests(TenantWorld world)
{
    private const string Eval = "/api/v1/evaluation";

    private async Task<(TestTenant Tenant, Guid Dataset)> FreshAsync(string[] locales)
    {
        var t = await world.CreateTenantAsync("eloc-" + Guid.NewGuid().ToString("N")[..8], "workplace_plus", "en", locales);
        await world.SeedUserAsync(t, "sara", MembershipTypes.Employee);
        await world.SeedUserAsync(t, "aip", MembershipTypes.Employee);
        var unit = await OkAsync(t["admin"].Client.PostAsJsonAsync("/api/v1/org/units",
            new { parentId = (Guid?)null, typeKey = "subsidiary", code = "Hr", names = new Dictionary<string, string> { ["en"] = "Hr" } }), HttpStatusCode.Created);
        var hr = unit.GetProperty("unit").GetProperty("id").GetGuid();
        t.Units["Hr"] = hr;
        await world.AssignAsync(t, "sara", BuiltInRoles.AgentDesigner, "org_subtree", hr);
        var dataset = (await OkAsync(t["sara"].Client.PostAsJsonAsync($"{Eval}/datasets", new { orgUnitId = hr, names = new { en = "Hr cases" } }), HttpStatusCode.Created))
            .GetProperty("id").GetGuid();
        return (t, dataset);
    }

    private static async Task<JsonElement> OkAsync(Task<HttpResponseMessage> call, HttpStatusCode expected = HttpStatusCode.OK)
    {
        var response = await call;
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == expected, $"{(int)response.StatusCode}: {text}");
        return text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone();
    }

    private static object Case(string input, string locale) => new { input, locale, expect = new { refusal = true }, tags = new[] { "hr" } };

    [Fact]
    public async Task Cases_in_the_later_locales_are_accepted_only_when_the_tenant_enabled_them()
    {
        var (t, dataset) = await FreshAsync(["en", "fr", "hi"]);
        var saved = await OkAsync(t["sara"].Client.PutAsJsonAsync($"{Eval}/datasets/{dataset}/cases",
            new { version = 0, cases = new[] { Case("Quel est le salaire d'Omar ?", "fr"), Case("उमर का वेतन कितना है?", "hi"), Case("What is Omar's salary?", "en") } }));
        Assert.Equal(["en", "fr", "hi"], saved.GetProperty("versions")[0].GetProperty("locales").EnumerateArray().Select(l => l.GetString()!).Order());
        var refused = await t["sara"].Client.PutAsJsonAsync($"{Eval}/datasets/{dataset}/cases", new { version = 1, cases = new[] { Case("عمر کی تنخواہ کتنی ہے؟", "ur") } });
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Contains("validation.eval.case_locale", await refused.Content.ReadAsStringAsync());

        // An import follows the same rule, row by row.
        var preview = await OkAsync(t["sara"].Client.PostAsJsonAsync($"{Eval}/datasets/{dataset}/import", new
        {
            format = "json",
            content = JsonSerializer.Serialize(new[] { Case("Quel est mon solde de congés ?", "fr"), Case("میری چھٹیاں کتنی ہیں؟", "ur") }),
        }));
        Assert.Equal(1, preview.GetProperty("valid").GetInt32());
        Assert.Contains(preview.GetProperty("errors").EnumerateArray(), e => e.GetProperty("key").GetString() == "validation.eval.case_locale");

        // A tenant with only the launch locales keeps Arabic and English.
        var (basic, basicSet) = await FreshAsync(["en", "ar"]);
        var launchOnly = await basic["sara"].Client.PutAsJsonAsync($"{Eval}/datasets/{basicSet}/cases", new { version = 0, cases = new[] { Case("Quel est le salaire d'Omar ?", "fr") } });
        Assert.Equal(HttpStatusCode.BadRequest, launchOnly.StatusCode);
    }
}
