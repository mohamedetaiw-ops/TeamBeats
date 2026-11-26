using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using TeamBeats.Audit.Contracts;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Testing;
using Xunit;

namespace TeamBeats.Integration.Tests;

/// <summary>
/// Spec 032 R3 FR-031 (T035, T036): components a tenant builds itself. Versions are checked and wait for the platform's
/// security review; only an approved version can be turned on and framed; the document is served under its own sandbox
/// policy with only the declared hosts; employees never see a version under review; a page holds at most three and only
/// available ones publish; the package decides; another tenant sees nothing.
/// </summary>
[Collection(WorldCollection.Name)]
public sealed class TenantComponentTests(TenantWorld world)
{
    private const string Components = "/api/v1/experience/tenant-components";
    private const string Reviews = "/api/v1/platform/component-reviews";
    private const string Host = "sandbox.tenant_component@1";
    private const string Document = "<!doctype html><p>Kudos</p><script>parent.postMessage({ type: 'tb.height', height: 200 }, '*')</script>";

    private static async Task<JsonElement> OkAsync(Task<HttpResponseMessage> call, HttpStatusCode expected = HttpStatusCode.OK)
    {
        var response = await call;
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == expected, $"{(int)response.StatusCode}: {text}");
        return text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone();
    }

    private static object Request(string id, string? document = null, string[]? hosts = null, string[]? context = null) => new
    {
        id, names = new { en = "Kudos wall", ar = "لوحة الشكر" }, hosts = hosts ?? ["https://api.example.com"], context = context ?? ["locale", "direction"],
        events = new[] { "cheered" }, height = 240, document = document ?? Document,
    };

    private async Task<TestTenant> FreshAsync(string package = "enterprise_gov")
    {
        var t = await world.CreateTenantAsync("tc-" + Guid.NewGuid().ToString("N")[..6], package);
        await world.SeedUserAsync(t, "emp", MembershipTypes.Employee);
        return t;
    }

    private async Task ApproveAsync(TestTenant t, string id, int version) =>
        await OkAsync(world.Platform.PostAsJsonAsync($"{Reviews}/{t.Id}/{id}/{version}", new { approve = true, note = "Checked by security" }), HttpStatusCode.NoContent);

    [Fact]
    public async Task A_version_is_reviewed_by_the_platform_before_it_can_be_turned_on_and_framed()
    {
        var t = await FreshAsync();
        var admin = t["admin"].Client;
        var employee = t["emp"].Client;
        Assert.True((await OkAsync(admin.GetAsync(Components))).GetProperty("available").GetBoolean());

        // The submission is checked.
        var bad = await admin.PostAsJsonAsync(Components, Request("X", new string('a', 300 * 1024), ["http://api.example.com/path"], ["email"]));
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        var problems = await bad.Content.ReadAsStringAsync();
        foreach (var key in new[] { "sandbox_id", "sandbox_hosts", "sandbox_context", "sandbox_document" })
        {
            Assert.Contains("validation.experience." + key, problems);
        }

        var submitted = await OkAsync(admin.PostAsJsonAsync(Components, Request("kudos-wall")));
        Assert.Equal("in_review", submitted.GetProperty("versions")[0].GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, submitted.GetProperty("live").ValueKind);
        await world.Database.AssertAuditedAsync(t.Id, AuditActions.ComponentSubmitted, "kudos-wall@1");
        Assert.Contains("sandbox_not_approved", await (await admin.PutAsJsonAsync($"{Components}/kudos-wall/enablement", new { enabled = true })).Content.ReadAsStringAsync());

        // Under review: employees see nothing, the manager can try it under the sandbox policy.
        Assert.Equal(HttpStatusCode.NotFound, (await employee.GetAsync("/api/v1/experience/sandbox/kudos-wall")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await employee.GetAsync("/api/v1/experience/sandbox/kudos-wall/1")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await employee.GetAsync(Components)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await employee.PostAsJsonAsync(Components, Request("other"))).StatusCode);
        var tried = await admin.GetAsync("/api/v1/experience/sandbox/kudos-wall/1");
        Assert.Equal(HttpStatusCode.OK, tried.StatusCode);
        var policy = tried.Headers.GetValues("Content-Security-Policy").Single();
        Assert.Contains("sandbox allow-scripts", policy);
        Assert.Contains("default-src 'none'", policy);
        Assert.Contains("connect-src https://api.example.com", policy);
        Assert.Contains("frame-ancestors 'self'", policy);
        Assert.DoesNotContain("allow-same-origin", policy);
        Assert.Equal("no-store", tried.Headers.CacheControl!.ToString());
        Assert.Equal(Document, await tried.Content.ReadAsStringAsync());

        // The platform reviews: the queue shows it, a rejection needs a reason, an approval is final.
        var queue = await OkAsync(world.Platform.GetAsync(Reviews));
        Assert.Contains(queue.EnumerateArray(), q => q.GetProperty("tenantId").GetGuid() == t.Id && q.GetProperty("componentId").GetString() == "kudos-wall");
        Assert.Equal(Document, await (await world.Platform.GetAsync($"{Reviews}/{t.Id}/kudos-wall/1/document")).Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.BadRequest, (await world.Platform.PostAsJsonAsync($"{Reviews}/{t.Id}/kudos-wall/1", new { approve = false })).StatusCode);
        Assert.NotEqual(HttpStatusCode.NoContent, (await admin.PostAsJsonAsync($"{Reviews}/{t.Id}/kudos-wall/1", new { approve = true })).StatusCode);
        await ApproveAsync(t, "kudos-wall", 1);
        Assert.Equal(HttpStatusCode.Conflict, (await world.Platform.PostAsJsonAsync($"{Reviews}/{t.Id}/kudos-wall/1", new { approve = false, note = "Changed my mind now" })).StatusCode);
        await world.Database.AssertAuditedAsync(t.Id, AuditActions.ComponentReviewed, "kudos-wall@1");

        // Turned on: members get the declared contract of the live version and its document.
        Assert.Equal(1, (await OkAsync(admin.PutAsJsonAsync($"{Components}/kudos-wall/enablement", new { enabled = true }))).GetProperty("live").GetInt32());
        var runtime = await OkAsync(employee.GetAsync("/api/v1/experience/sandbox/kudos-wall"));
        Assert.Equal(1, runtime.GetProperty("version").GetInt32());
        Assert.Equal(["locale", "direction"], runtime.GetProperty("context").EnumerateArray().Select(c => c.GetString()));
        Assert.Equal(HttpStatusCode.OK, (await employee.GetAsync("/api/v1/experience/sandbox/kudos-wall/1")).StatusCode);

        // A new version waits for review while the approved one stays live.
        await OkAsync(admin.PostAsJsonAsync(Components, Request("kudos-wall", "<!doctype html><p>Kudos 2</p>")));
        Assert.Equal(1, (await OkAsync(employee.GetAsync("/api/v1/experience/sandbox/kudos-wall"))).GetProperty("version").GetInt32());
        Assert.Equal(HttpStatusCode.NotFound, (await employee.GetAsync("/api/v1/experience/sandbox/kudos-wall/2")).StatusCode);

        // Another tenant sees nothing of it.
        var other = await FreshAsync();
        Assert.Equal(HttpStatusCode.NotFound, (await other["admin"].Client.GetAsync("/api/v1/experience/sandbox/kudos-wall")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other["admin"].Client.GetAsync("/api/v1/experience/sandbox/kudos-wall/1")).StatusCode);
        Assert.Empty((await OkAsync(other["admin"].Client.GetAsync(Components))).GetProperty("components").EnumerateArray());
    }

    private static JsonObject Framed(string id, string component) =>
        new() { ["id"] = id, ["componentRef"] = Host, ["props"] = new JsonObject { ["component"] = component } };

    private static async Task<HttpResponseMessage> SaveAsync(TestTenant t, JsonNode page, params JsonObject[] placements)
    {
        var draft = page["draft"]!.DeepClone();
        draft["regions"]!.AsArray().First()!["placements"] = new JsonArray(placements.Select(p => (JsonNode)p).ToArray());
        var request = new HttpRequestMessage(HttpMethod.Put, $"/api/v1/experience/pages/{page["id"]}/draft") { Content = JsonContent.Create(new { definition = draft }) };
        request.Headers.TryAddWithoutValidation("If-Match", page["eTag"]!.GetValue<string>());
        return await t["admin"].Client.SendAsync(request);
    }

    private static async Task<string[]> CodesAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        return [.. (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("findings").EnumerateArray().Select(f => f.GetProperty("code").GetString()!)];
    }

    [Fact]
    public async Task A_page_frames_at_most_three_and_only_available_components_publish_and_the_package_decides()
    {
        var t = await FreshAsync();
        await OkAsync(t["admin"].Client.PostAsJsonAsync(Components, Request("kudos-wall")));
        await ApproveAsync(t, "kudos-wall", 1);
        await OkAsync(t["admin"].Client.PutAsJsonAsync($"{Components}/kudos-wall/enablement", new { enabled = true }));
        var page = JsonNode.Parse(await (await t["admin"].Client.PostAsJsonAsync("/api/v1/experience/pages",
            new { templateId = "T01", slug = "tc-" + Guid.NewGuid().ToString("N")[..8], orgUnitId = (Guid?)null })).Content.ReadAsStringAsync())!;

        Assert.Contains("validation.page.sandbox_limit", await CodesAsync(await SaveAsync(t, page,
            Framed("a", "kudos-wall"), Framed("b", "kudos-wall"), Framed("c", "kudos-wall"), Framed("d", "kudos-wall"))));

        // A component that is not available is a warning while saving and blocks publishing.
        var saved = await SaveAsync(t, page, Framed("a", "kudos-wall"), Framed("b", "missing"));
        Assert.True(saved.IsSuccessStatusCode, await saved.Content.ReadAsStringAsync());
        Assert.Contains("validation.page.sandbox_unavailable",
            await CodesAsync(await t["admin"].Client.PostAsJsonAsync($"/api/v1/experience/pages/{page["id"]}/publish", new { reason = "first version" })));

        // Turned off, the component is no longer available to pages either.
        await OkAsync(t["admin"].Client.PutAsJsonAsync($"{Components}/kudos-wall/enablement", new { enabled = false }));
        Assert.Equal(HttpStatusCode.NotFound, (await t["emp"].Client.GetAsync("/api/v1/experience/sandbox/kudos-wall")).StatusCode);

        // Without the package feature nothing can be submitted, and tenants cannot reach the platform review.
        var plus = await FreshAsync("workplace_plus");
        Assert.False((await OkAsync(plus["admin"].Client.GetAsync(Components))).GetProperty("available").GetBoolean());
        Assert.Equal(HttpStatusCode.Forbidden, (await plus["admin"].Client.PostAsJsonAsync(Components, Request("kudos-wall"))).StatusCode);
        Assert.NotEqual(HttpStatusCode.OK, (await t["admin"].Client.GetAsync(Reviews)).StatusCode);
    }
}
