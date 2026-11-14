using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Testing;
using Xunit;

namespace TeamBeats.Integration.Tests;

/// <summary>
/// Spec 016 R2 T025 (section 12): the starter template pack. A tenant-wide catalogue owner switches a template on and
/// gets a tenant-owned draft that passes the same checks as any draft and can be published; switching on again returns
/// the same service; templates outside the plan cannot be switched on; employees and other tenants see nothing of it.
/// </summary>
[Collection(WorldCollection.Name)]
public sealed class ServiceTemplateTests(TenantWorld world)
{
    private const string Admin = "/api/v1/services/admin";

    private static async Task<JsonElement[]> ListAsync(HttpClient client) =>
        (await (await client.GetAsync($"{Admin}/templates")).ReadAsync<JsonElement>()).EnumerateArray().ToArray();

    [Fact]
    public async Task Every_available_template_switches_on_as_a_valid_draft_once()
    {
        var t = await world.CreateTenantAsync("tpl-" + Guid.NewGuid().ToString("N")[..8], "workplace_plus");
        var templates = await ListAsync(t["admin"].Client);
        Assert.Equal(12, templates.Length);
        Assert.All(templates, x => Assert.Equal(JsonValueKind.Null, x.GetProperty("serviceId").ValueKind));
        Assert.Contains(templates, x => x.GetProperty("key").GetString() == "resignation" && x.GetProperty("available").GetBoolean());

        foreach (var template in templates)
        {
            var key = template.GetProperty("key").GetString()!;
            var response = await t["admin"].Client.PostAsync($"{Admin}/templates/{key}", null);
            if (!template.GetProperty("available").GetBoolean())
            {
                Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
                continue;
            }
            Assert.True(response.StatusCode == HttpStatusCode.Created, $"{key}: {await response.Content.ReadAsStringAsync()}");
            var view = await response.ReadAsync<JsonElement>();
            Assert.Equal("draft", view.GetProperty("service").GetProperty("status").GetString());
            Assert.Equal(JsonValueKind.Null, view.GetProperty("service").GetProperty("ownerOrgUnitId").ValueKind);
            Assert.Equal(JsonValueKind.Object, view.GetProperty("latest").GetProperty("route").GetProperty("graph").ValueKind);

            // Switching on again returns the same service.
            var again = await t["admin"].Client.PostAsync($"{Admin}/templates/{key}", null);
            Assert.Equal(HttpStatusCode.OK, again.StatusCode);
            Assert.Equal(view.GetProperty("service").GetProperty("id").GetGuid(), (await again.ReadAsync<JsonElement>()).GetProperty("service").GetProperty("id").GetGuid());
        }

        var listed = await ListAsync(t["admin"].Client);
        Assert.All(listed.Where(x => x.GetProperty("available").GetBoolean()), x => Assert.Equal("draft", x.GetProperty("serviceStatus").GetString()));
        var resignation = listed.Single(x => x.GetProperty("key").GetString() == "resignation").GetProperty("serviceId").GetGuid();
        var detail = await (await t["admin"].Client.GetAsync($"{Admin}/{resignation}")).ReadAsync<JsonElement>();
        Assert.True(detail.GetProperty("latest").GetProperty("route").GetProperty("continueAfterExit").GetBoolean());
        Assert.Equal("confidential", detail.GetProperty("service").GetProperty("classification").GetString());
    }

    [Fact]
    public async Task A_switched_on_template_publishes_and_a_colleague_files_it_with_prefill()
    {
        var t = await world.CreateTenantAsync("tpl-" + Guid.NewGuid().ToString("N")[..8], "workplace_plus");
        await world.SeedUserAsync(t, "sara", MembershipTypes.Employee);
        var created = await t["admin"].Client.PostAsync($"{Admin}/templates/business_cards", null);
        var id = (await created.ReadAsync<JsonElement>()).GetProperty("service").GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.OK, (await t["admin"].Client.PostAsJsonAsync($"{Admin}/{id}/publish", new { })).StatusCode);

        var prefill = await (await t["sara"].Client.GetAsync($"/api/v1/services/{id}/prefill")).ReadAsync<JsonElement>();
        Assert.Equal(t["sara"].Email, prefill.GetProperty("values").GetProperty("email").GetString());
        var submitted = await t["sara"].Client.PostAsJsonAsync("/api/v1/requests", new
        {
            serviceId = id,
            data = new { full_name = "Sara Example", job_title = "Analyst", email = "sara@example.com", include_mobile = "no", boxes = 2 },
        });
        Assert.True(submitted.StatusCode == HttpStatusCode.Created, await submitted.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Only_tenant_wide_owners_see_or_switch_on_templates_and_tenants_stay_apart()
    {
        var t = await world.CreateTenantAsync("tpl-" + Guid.NewGuid().ToString("N")[..8], "workplace_plus");
        await world.SeedUserAsync(t, "sara", MembershipTypes.Employee);
        Assert.Equal(HttpStatusCode.Forbidden, (await t["sara"].Client.GetAsync($"{Admin}/templates")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await t["sara"].Client.PostAsync($"{Admin}/templates/business_cards", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await t["admin"].Client.PostAsync($"{Admin}/templates/no_such_template", null)).StatusCode);

        Assert.Equal(HttpStatusCode.Created, (await t["admin"].Client.PostAsync($"{Admin}/templates/referral", null)).StatusCode);
        var other = await world.CreateTenantAsync("tpl-" + Guid.NewGuid().ToString("N")[..8], "workplace_plus");
        var theirs = await ListAsync(other["admin"].Client);
        Assert.Equal(JsonValueKind.Null, theirs.Single(x => x.GetProperty("key").GetString() == "referral").GetProperty("serviceId").ValueKind);
    }
}
