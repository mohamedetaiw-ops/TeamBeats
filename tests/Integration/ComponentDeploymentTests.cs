using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using TeamBeats.Audit.Contracts;
using TeamBeats.Testing;
using Xunit;

namespace TeamBeats.Integration.Tests;

/// <summary>
/// Spec 032 R2 T031 (FR-033, US7 scenario 2): upgrading a component to its next major deploys it to the tenant's live pages
/// without republishing them. The migration hook converts placements as pages are read, a placement the new major would reject
/// keeps the old one, pages that do not use the component read exactly as before, and a rollback brings the old major back.
/// Other tenants keep the major they had.
/// </summary>
[Collection(WorldCollection.Name)]
public sealed class ComponentDeploymentTests(TenantWorld world)
{
    private const string Experience = "/api/v1/experience";
    private const string Gauge = UpgradePack.Id;

    private static readonly object Text = new { body = new { en = "The canteen opens at eight.", ar = "يفتح المقصف في الثامنة." } };

    private static async Task<JsonElement> OkAsync(Task<HttpResponseMessage> call)
    {
        var response = await call;
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"{(int)response.StatusCode}: {text}");
        return text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone();
    }

    private static JsonObject P(string id, string component, object props) =>
        new() { ["id"] = id, ["componentRef"] = component, ["props"] = JsonSerializer.SerializeToNode(props) };

    /// <summary>A published T01 page whose main region holds <paramref name="placements"/>.</summary>
    private static async Task<JsonNode> PublishedAsync(HttpClient admin, params JsonObject[] placements)
    {
        var created = await admin.PostAsJsonAsync($"{Experience}/pages", new { templateId = "T01", slug = "cd-" + Guid.NewGuid().ToString("N")[..10] });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var page = JsonNode.Parse(await created.Content.ReadAsStringAsync())!;
        var draft = page["draft"]!.DeepClone();
        draft["regions"]!.AsArray().First(r => r!["id"]!.GetValue<string>() == "main")!["placements"] = new JsonArray(placements.Select(p => (JsonNode)p).ToArray());
        var request = new HttpRequestMessage(HttpMethod.Put, $"{Experience}/pages/{page["id"]}/draft") { Content = JsonContent.Create(new { definition = draft }) };
        request.Headers.TryAddWithoutValidation("If-Match", page["eTag"]!.GetValue<string>());
        await OkAsync(admin.SendAsync(request));
        await OkAsync(admin.PostAsJsonAsync($"{Experience}/pages/{page["id"]}/publish", new { reason = "first" }));
        return page;
    }

    private static Task<JsonElement> RenderAsync(HttpClient viewer, JsonNode page) => OkAsync(viewer.GetAsync($"{Experience}/render?slug={page["slug"]}"));

    private static JsonElement Placement(JsonElement rendered, string id) => rendered.GetProperty("regions").EnumerateArray()
        .SelectMany(r => r.GetProperty("placements").EnumerateArray()).Single(p => p.GetProperty("id").GetString() == id);

    private static async Task<int> ReleaseCountAsync(HttpClient admin, JsonNode page) =>
        (await OkAsync(admin.GetAsync($"{Experience}/pages/{page["id"]}/releases"))).GetArrayLength();

    [Fact]
    public async Task An_upgrade_reaches_live_pages_without_a_republish_and_a_rollback_brings_the_old_major_back()
    {
        var t = await world.CreateTenantAsync("cd-" + Guid.NewGuid().ToString("N")[..8], "workplace_plus");
        await world.SeedUserAsync(t, "employee", "employee");
        var admin = t["admin"].Client;
        var uses = await PublishedAsync(admin, P("g", Gauge + "@1", new { label = "Open tickets", unit = "count" }), P("t", "reference.alpha.text@1", Text));
        var tooLong = await PublishedAsync(admin, P("long", Gauge + "@1", new { label = "A label far longer than the new version allows", unit = "percent" }));
        var other = await PublishedAsync(admin, P("t", "reference.alpha.text@1", Text));
        var otherBefore = (await RenderAsync(t["employee"].Client, other)).GetRawText();
        Assert.Equal(Gauge + "@1", Placement(await RenderAsync(t["employee"].Client, uses), "g").GetProperty("componentRef").GetString());

        // Another tenant places the same major and never takes part in this tenant's deployment.
        var theirs = await PublishedAsync(world.B["admin"].Client, P("g", Gauge + "@1", new { label = "Theirs", unit = "count" }));

        var before = await OkAsync(admin.GetAsync($"{Experience}/components/deployments"));
        Assert.Empty(before.GetProperty("deployed").EnumerateArray());
        var offered = before.GetProperty("available").EnumerateArray().Single(o => o.GetProperty("componentId").GetString() == Gauge);
        Assert.Equal(Gauge + "@2", offered.GetProperty("to").GetString());
        Assert.False(offered.GetProperty("deployed").GetBoolean());
        Assert.False(string.IsNullOrEmpty(offered.GetProperty("name").GetString()));

        await OkAsync(admin.PostAsJsonAsync($"{Experience}/components/{Gauge}/upgrade", new { fromMajor = 1 }));
        var after = await OkAsync(admin.GetAsync($"{Experience}/components/deployments"));
        Assert.True(after.GetProperty("available").EnumerateArray().Single(o => o.GetProperty("componentId").GetString() == Gauge).GetProperty("deployed").GetBoolean());
        var deployment = Assert.Single(after.GetProperty("deployed").EnumerateArray());
        Assert.Equal(Gauge + "@1", deployment.GetProperty("from").GetString());
        Assert.Equal(Gauge + "@2", deployment.GetProperty("to").GetString());

        // The live release shows the new major through the hook, and no release was added.
        var shown = Placement(await RenderAsync(t["employee"].Client, uses), "g");
        Assert.Equal(Gauge + "@2", shown.GetProperty("componentRef").GetString());
        Assert.Equal("ready", shown.GetProperty("state").GetString());
        Assert.Equal("number", shown.GetProperty("props").GetProperty("format").GetString());
        Assert.False(shown.GetProperty("props").TryGetProperty("unit", out _));
        Assert.Equal("reference.alpha.text@1", Placement(await RenderAsync(t["employee"].Client, uses), "t").GetProperty("componentRef").GetString());
        Assert.Equal(1, await ReleaseCountAsync(admin, uses));

        // A placement the new major would reject keeps the old one; a page without the component reads exactly as before.
        Assert.Equal(Gauge + "@1", Placement(await RenderAsync(t["employee"].Client, tooLong), "long").GetProperty("componentRef").GetString());
        Assert.Equal(otherBefore, (await RenderAsync(t["employee"].Client, other)).GetRawText());
        Assert.Equal(Gauge + "@1", Placement(await RenderAsync(world.B["admin"].Client, theirs), "g").GetProperty("componentRef").GetString());
        Assert.Empty((await OkAsync(world.B["admin"].Client.GetAsync($"{Experience}/components/deployments"))).GetProperty("deployed").EnumerateArray());

        // Deployments need the component permission, and another tenant cannot roll this one back.
        Assert.Equal(HttpStatusCode.Forbidden, (await t["employee"].Client.GetAsync($"{Experience}/components/deployments")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await t["employee"].Client.DeleteAsync($"{Experience}/components/{Gauge}/deployments/1")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await world.B["admin"].Client.DeleteAsync($"{Experience}/components/{Gauge}/deployments/1")).StatusCode);

        // Rolling back shows the old major on the unchanged release again, and is audited.
        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync($"{Experience}/components/{Gauge}/deployments/1")).StatusCode);
        await world.Database.AssertAuditedAsync(t.Id, AuditActions.ComponentRolledBack, Gauge);
        var back = Placement(await RenderAsync(t["employee"].Client, uses), "g");
        Assert.Equal(Gauge + "@1", back.GetProperty("componentRef").GetString());
        Assert.Equal("count", back.GetProperty("props").GetProperty("unit").GetString());
        Assert.Equal(1, await ReleaseCountAsync(admin, uses));
        Assert.Equal(HttpStatusCode.NotFound, (await admin.DeleteAsync($"{Experience}/components/{Gauge}/deployments/1")).StatusCode);
    }
}
