using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using TeamBeats.Audit.Contracts;
using TeamBeats.Entitlements.Contracts;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Testing;
using Xunit;

namespace TeamBeats.Integration.Tests;

/// <summary>
/// Spec 032 R2 T030 (FR-057): a tenant administrator switches components and templates on or off for the tenant, an org unit
/// or (components) an audience, as versioned configuration that the picker, validation, page creation and the renderer read.
/// A switch never turns on what the package leaves out; other tenants, other people and packages without controls reach nothing.
/// </summary>
[Collection(WorldCollection.Name)]
public sealed class FeatureControlTests(TenantWorld world)
{
    private const string Experience = "/api/v1/experience";
    private const string Text = "reference.alpha.text";

    private static readonly object Props = new { body = new { en = "Shift times change on Sunday.", ar = "تتغير مواعيد الورديات يوم الأحد." } };

    private async Task<TestTenant> FreshAsync(string package = "workplace_plus")
    {
        var t = await world.CreateTenantAsync("fc-" + Guid.NewGuid().ToString("N")[..8], package);
        var holding = await world.CreateUnitAsync(t, "Holding", null, "organization");
        var gulf = await world.CreateUnitAsync(t, "Gulf", holding, "subsidiary");
        await world.CreateUnitAsync(t, "Plant", gulf, "department");
        await world.CreateUnitAsync(t, "Africa", holding, "subsidiary");
        foreach (var (name, unit) in new[] { ("gulfy", "Gulf"), ("afro", "Africa") })
        {
            await world.SeedUserAsync(t, name, MembershipTypes.Employee);
            await world.AddMembershipAsync(t, name, unit);
        }
        return t;
    }

    private static async Task<JsonElement> OkAsync(Task<HttpResponseMessage> call)
    {
        var response = await call;
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"{(int)response.StatusCode}: {text}");
        return text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone();
    }

    private static Task<HttpResponseMessage> SwitchAsync(HttpClient client, string kind, string subject, bool enabled, Guid? unit = null, Guid? audience = null) =>
        client.PutAsJsonAsync($"{Experience}/feature-controls", new { subjectKind = kind, subjectId = subject, orgUnitId = unit, audienceId = audience, enabled });

    /// <summary>A page of <paramref name="unit"/> holding one text placement; published when asked.</summary>
    private static async Task<JsonNode> PageAsync(HttpClient admin, Guid? unit, bool publish = false)
    {
        var created = await admin.PostAsJsonAsync($"{Experience}/pages", new { templateId = "T01", slug = "fc-" + Guid.NewGuid().ToString("N")[..10], orgUnitId = unit });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var page = JsonNode.Parse(await created.Content.ReadAsStringAsync())!;
        var draft = page["draft"]!.DeepClone();
        draft["regions"]!.AsArray().First(r => r!["id"]!.GetValue<string>() == "main")!["placements"] =
            new JsonArray(new JsonObject { ["id"] = "intro", ["componentRef"] = Text + "@1", ["props"] = JsonSerializer.SerializeToNode(Props) });
        var request = new HttpRequestMessage(HttpMethod.Put, $"{Experience}/pages/{page["id"]}/draft") { Content = JsonContent.Create(new { definition = draft }) };
        request.Headers.TryAddWithoutValidation("If-Match", page["eTag"]!.GetValue<string>());
        var saved = await admin.SendAsync(request);
        Assert.True(saved.IsSuccessStatusCode, await saved.Content.ReadAsStringAsync());
        if (publish)
        {
            await OkAsync(admin.PostAsJsonAsync($"{Experience}/pages/{page["id"]}/publish", new { reason = "first" }));
        }
        return page;
    }

    private static async Task<string?> PickerReasonAsync(HttpClient admin, JsonNode page) =>
        (await OkAsync(admin.GetAsync($"{Experience}/pages/{page["id"]}/components?region=main"))).EnumerateArray()
            .Single(i => i.GetProperty("componentRef").GetString() == Text + "@1").GetProperty("reasonKey").GetString();

    private static async Task<string> StateAsync(HttpClient viewer, JsonNode page) =>
        (await OkAsync(viewer.GetAsync($"{Experience}/render?slug={page["slug"]}"))).GetProperty("regions").EnumerateArray()
            .First(r => r.GetProperty("id").GetString() == "main").GetProperty("placements")[0].GetProperty("state").GetString()!;

    [Fact]
    public async Task Switches_for_a_unit_and_below_turn_a_component_off_and_the_nearest_one_wins()
    {
        var t = await FreshAsync();
        var admin = t["admin"].Client;
        var plant = await PageAsync(admin, t.Unit("Plant"));
        var africa = await PageAsync(admin, t.Unit("Africa"));
        Assert.Null(await PickerReasonAsync(admin, plant));

        // Off for the Gulf: its pages and those below it cannot hold the component; Africa is untouched.
        var gulfOff = await OkAsync(SwitchAsync(admin, "component", Text, false, t.Unit("Gulf")));
        var controlId = gulfOff.GetProperty("id").GetGuid();
        Assert.Equal(1, gulfOff.GetProperty("version").GetInt32());
        await world.Database.AssertAuditedAsync(t.Id, AuditActions.FeatureControlChanged, controlId.ToString());
        Assert.Equal("validation.page.component_disabled", await PickerReasonAsync(admin, plant));
        Assert.Null(await PickerReasonAsync(admin, africa));
        var findings = await OkAsync(admin.PostAsync($"{Experience}/pages/{plant["id"]}/validate", null));
        Assert.Contains(findings.GetProperty("findings").EnumerateArray(), f => f.GetProperty("code").GetString() == "validation.page.component_disabled");

        // On again for the Plant, below the Gulf: the nearest switch wins.
        await OkAsync(SwitchAsync(admin, "component", Text, true, t.Unit("Plant")));
        Assert.Null(await PickerReasonAsync(admin, plant));

        // Off for the whole tenant, Africa follows; the Plant's own switch still wins there.
        await OkAsync(SwitchAsync(admin, "component", Text, false));
        Assert.Equal("validation.page.component_disabled", await PickerReasonAsync(admin, africa));
        Assert.Null(await PickerReasonAsync(admin, plant));

        // Every change is a version; removing a switch is one too, and the next switch up applies again.
        var changed = await OkAsync(SwitchAsync(admin, "component", Text, true, t.Unit("Gulf")));
        Assert.Equal(controlId, changed.GetProperty("id").GetGuid());
        Assert.Equal(2, changed.GetProperty("version").GetInt32());
        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync($"{Experience}/feature-controls/{controlId}")).StatusCode);
        var versions = (await OkAsync(admin.GetAsync($"{Experience}/feature-controls/{controlId}/versions"))).EnumerateArray().ToList();
        Assert.Equal([3, 2, 1], versions.Select(v => v.GetProperty("version").GetInt32()));
        Assert.Equal(JsonValueKind.Null, versions[0].GetProperty("enabled").ValueKind);
        Assert.False(versions[2].GetProperty("enabled").GetBoolean());
        Assert.DoesNotContain((await OkAsync(admin.GetAsync($"{Experience}/feature-controls"))).EnumerateArray(), c => c.GetProperty("id").GetGuid() == controlId);
    }

    [Fact]
    public async Task An_audience_switch_changes_what_its_viewers_see_and_templates_switch_for_new_pages()
    {
        var t = await FreshAsync();
        var admin = t["admin"].Client;
        var page = await PageAsync(admin, null, publish: true);
        var rule = Guid.NewGuid();
        await OkAsync(admin.PutAsJsonAsync($"{Experience}/audience-rules/{rule}",
            new { labels = new { en = "Africa", ar = "أفريقيا" }, conditions = new { orgUnits = new[] { t.Unit("Africa") } } }));

        // Off for the Africa audience: its people see the placement as unavailable, others see it.
        await OkAsync(SwitchAsync(admin, "component", Text, false, audience: rule));
        Assert.Equal("unavailable", await StateAsync(t["afro"].Client, page));
        Assert.Equal("ready", await StateAsync(t["gulfy"].Client, page));
        Assert.Equal(HttpStatusCode.BadRequest, (await SwitchAsync(admin, "template", "T01", false, audience: rule)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await SwitchAsync(admin, "component", Text, false, t.Unit("Gulf"), rule)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await SwitchAsync(admin, "component", "no.such.component", false)).StatusCode);

        // A template off for the Gulf takes no new pages there; Africa still creates them, and existing pages keep working.
        await OkAsync(SwitchAsync(admin, "template", "T03", false, t.Unit("Gulf")));
        var refused = await admin.PostAsJsonAsync($"{Experience}/pages", new { templateId = "T03", slug = "fc-" + Guid.NewGuid().ToString("N")[..10], orgUnitId = t.Unit("Plant") });
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Contains("validation.page.template_switched_off", await refused.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.Created, (await admin.PostAsJsonAsync($"{Experience}/pages",
            new { templateId = "T03", slug = "fc-" + Guid.NewGuid().ToString("N")[..10], orgUnitId = t.Unit("Africa") })).StatusCode);
        // Off for the whole tenant, it leaves the chooser.
        Assert.Contains((await OkAsync(admin.GetAsync($"{Experience}/catalogue"))).GetProperty("templates").EnumerateArray(), x => x.GetProperty("id").GetString() == "T02");
        await OkAsync(SwitchAsync(admin, "template", "T02", false));
        Assert.DoesNotContain((await OkAsync(admin.GetAsync($"{Experience}/catalogue"))).GetProperty("templates").EnumerateArray(), x => x.GetProperty("id").GetString() == "T02");
    }

    [Fact]
    public async Task A_switch_never_turns_on_what_the_package_leaves_out_and_only_tenant_administrators_set_them()
    {
        // Workplace has neither feature controls nor Microsoft 365. Changing a package's capability briefly publishes a version for
        // every tenant created meanwhile, so this turns a flag on (harmless to other tests) rather than one off.
        var basic = await FreshAsync("workplace");
        var admin = basic["admin"].Client;
        Assert.Equal(HttpStatusCode.Forbidden, (await SwitchAsync(admin, "component", Text, false)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await admin.GetAsync($"{Experience}/feature-controls")).StatusCode);
        var page = await PageAsync(admin, null);
        await world.SetCapabilityAsync(basic, Capabilities.ExperienceFeatureControls, new { enabled = true });

        var refused = await SwitchAsync(admin, "component", "reference.beta.counter", true);
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Contains("validation.feature_control.not_in_package", await refused.Content.ReadAsStringAsync());
        // Turning it off is allowed: it changes nothing the package gives.
        var id = (await OkAsync(SwitchAsync(admin, "component", "reference.beta.counter", false))).GetProperty("id").GetGuid();
        await OkAsync(SwitchAsync(admin, "component", Text, false));
        Assert.Equal("validation.page.component_disabled", await PickerReasonAsync(admin, page));

        // Other people of the tenant and other tenants reach nothing.
        Assert.Equal(HttpStatusCode.Forbidden, (await SwitchAsync(basic["gulfy"].Client, "component", Text, false)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await basic["gulfy"].Client.GetAsync($"{Experience}/feature-controls")).StatusCode);
        var other = await FreshAsync();
        Assert.Equal(HttpStatusCode.NotFound, (await other["admin"].Client.GetAsync($"{Experience}/feature-controls/{id}/versions")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other["admin"].Client.DeleteAsync($"{Experience}/feature-controls/{id}")).StatusCode);
        Assert.Empty((await OkAsync(other["admin"].Client.GetAsync($"{Experience}/feature-controls"))).EnumerateArray());

        // Back on a package without feature controls, switches already set are not read.
        await world.SetCapabilityAsync(basic, Capabilities.ExperienceFeatureControls, new { enabled = false });
        Assert.Null(await PickerReasonAsync(admin, page));
    }
}
