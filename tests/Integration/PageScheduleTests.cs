using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using TeamBeats.Audit.Contracts;
using TeamBeats.Modules.Experience.Pages;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Persistence;
using TeamBeats.TenantContext;
using TeamBeats.Testing;
using Xunit;

namespace TeamBeats.Integration.Tests;

/// <summary>
/// Spec 032 R2 T025 (US6 scenario 4, FR-053): scheduled page releases go live and expire at tenant-local times. The
/// release is validated again at go-live; one that fails does not go live, the release in force stays and the publisher
/// is notified. At the expiry the release in force before returns, or the page goes offline when it had none. Scheduling
/// needs page.publish at the page's scope; other units and tenants find nothing; pages under an approval route go live
/// through approval.
/// </summary>
[Collection(WorldCollection.Name)]
public sealed class PageScheduleTests(TenantWorld world)
{
    private const string Pages = "/api/v1/experience/pages";

    private async Task<TestTenant> FreshAsync(string package = "workplace_plus")
    {
        var t = await world.CreateTenantAsync("psc-" + Guid.NewGuid().ToString("N")[..8], package);
        var holding = await world.CreateUnitAsync(t, "Holding", null, "organization");
        var gulf = await world.CreateUnitAsync(t, "Gulf", holding, "subsidiary");
        var africa = await world.CreateUnitAsync(t, "Africa", holding, "subsidiary");
        foreach (var name in new[] { "publisher", "editor", "far" })
        {
            await world.SeedUserAsync(t, name, MembershipTypes.Employee);
            await world.AddMembershipAsync(t, name, name == "far" ? "Africa" : "Gulf");
        }
        await world.AssignAsync(t, "publisher", "page_publisher", "org_subtree", gulf);
        await world.AssignAsync(t, "editor", "page_editor", "org_subtree", gulf);
        await world.AssignAsync(t, "far", "page_publisher", "org_subtree", africa);
        return t;
    }

    private static async Task<JsonElement> OkAsync(Task<HttpResponseMessage> call)
    {
        var response = await call;
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"{(int)response.StatusCode}: {text}");
        return text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone();
    }

    private static async Task<string> KeyAsync(HttpResponseMessage response)
    {
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        if (body.TryGetProperty("errors", out var errors))
        {
            return errors.EnumerateObject().First().Value[0].GetProperty("key").GetString()!;
        }
        return body.TryGetProperty("detailKey", out var detail) ? detail.GetString()! : body.GetProperty("code").GetString()!;
    }

    private static async Task<JsonNode> PageAsync(TestTenant t, string user, Guid unit, string text)
    {
        var created = await t[user].Client.PostAsJsonAsync(Pages, new { templateId = "T01", slug = "s-" + Guid.NewGuid().ToString("N")[..10], orgUnitId = unit });
        Assert.True(created.StatusCode == HttpStatusCode.Created, await created.Content.ReadAsStringAsync());
        return await SaveAsync(t, user, JsonNode.Parse(await created.Content.ReadAsStringAsync())!, text);
    }

    private static async Task<JsonNode> SaveAsync(TestTenant t, string user, JsonNode page, string text)
    {
        var draft = page["draft"]!.DeepClone();
        draft["regions"]!.AsArray().First(r => r!["id"]!.GetValue<string>() == "main")!["placements"] = new JsonArray(new JsonObject
        {
            ["id"] = "intro", ["componentRef"] = "reference.alpha.text@1",
            ["props"] = JsonSerializer.SerializeToNode(new { body = new { en = text, ar = text } }),
        });
        var request = new HttpRequestMessage(HttpMethod.Put, $"{Pages}/{page["id"]}/draft") { Content = JsonContent.Create(new { definition = draft }) };
        request.Headers.TryAddWithoutValidation("If-Match", page["eTag"]!.GetValue<string>());
        var response = await t[user].Client.SendAsync(request);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
    }

    private Task<int> RunAsync(TestTenant t, DateTimeOffset now) =>
        world.Factory.InScopeAsync(t.Id, async sp =>
        {
            sp.GetRequiredService<TenantContextAccessor>().SetActor(ActorType.System, null);
            var moved = await sp.GetRequiredService<PageSchedules>().RunDueAsync(now, 200, CancellationToken.None);
            await sp.GetRequiredService<IDbSession>().CommitAsync();
            return moved;
        });

    private Task<int?> CurrentAsync(TestTenant t, string id) =>
        world.Database.TenantScalarAsync<int?>(t.Id, "select current_release from experience.page where id = @id", ("id", Guid.Parse(id)));

    private static object At(DateTime local) => local.ToString("yyyy-MM-ddTHH:mm:ss", System.Globalization.CultureInfo.InvariantCulture);

    [Fact]
    public async Task A_scheduled_release_goes_live_at_its_tenant_local_time_and_the_earlier_release_returns_at_the_expiry()
    {
        var t = await FreshAsync();
        var client = t["publisher"].Client;
        var page = await PageAsync(t, "publisher", t.Unit("Gulf"), "Old shift times.");
        var id = page["id"]!.GetValue<string>();
        await OkAsync(client.PostAsJsonAsync($"{Pages}/{id}/publish", new { reason = "first" }));
        await SaveAsync(t, "publisher", JsonNode.Parse(await (await client.GetAsync($"{Pages}/{id}")).Content.ReadAsStringAsync())!, "New shift times.");

        var tomorrow = DateTime.SpecifyKind(DateTime.UtcNow.Date.AddDays(2), DateTimeKind.Unspecified);
        var goLiveLocal = tomorrow.AddHours(9);
        var endLocal = tomorrow.AddDays(1).AddHours(18);
        var view = await OkAsync(client.PutAsJsonAsync($"{Pages}/{id}/schedule", new { goLiveAt = At(goLiveLocal), expiresAt = At(endLocal), reason = "Ramadan hours" }));
        Assert.Equal("scheduled", view.GetProperty("status").GetString());
        var zone = TimeZoneInfo.FindSystemTimeZoneById(view.GetProperty("timeZone").GetString()!);
        var goLive = new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(goLiveLocal, zone), TimeSpan.Zero);
        var end = new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(endLocal, zone), TimeSpan.Zero);
        Assert.Equal(goLive, view.GetProperty("goLiveAt").GetDateTimeOffset());
        Assert.Equal(end, view.GetProperty("expiresAt").GetDateTimeOffset());
        await world.Database.AssertAuditedAsync(t.Id, AuditActions.PageScheduled, id);

        // A later edit of the draft does not change what was scheduled; a second schedule waits for this one.
        await SaveAsync(t, "publisher", JsonNode.Parse(await (await client.GetAsync($"{Pages}/{id}")).Content.ReadAsStringAsync())!, "Edited after scheduling.");
        var second = await client.PutAsJsonAsync($"{Pages}/{id}/schedule", new { goLiveAt = At(goLiveLocal.AddHours(1)) });
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        Assert.Equal("conflict.page.scheduled", await KeyAsync(second));

        Assert.Equal(0, await RunAsync(t, goLive.AddMinutes(-1)));
        Assert.Equal(1, await CurrentAsync(t, id));
        Assert.Equal(1, await RunAsync(t, goLive.AddSeconds(30)));
        Assert.Equal(2, await CurrentAsync(t, id));
        var release = (await OkAsync(client.GetAsync($"{Pages}/{id}/releases"))).EnumerateArray().First(r => r.GetProperty("release").GetInt32() == 2);
        Assert.Equal(t["publisher"].Id, release.GetProperty("publishedBy").GetGuid());
        Assert.Equal("New shift times.", await world.Database.TenantScalarAsync<string>(t.Id,
            "select jsonb_path_query_first(definition, '$.regions[*] ? (@.id == \"main\").placements[0].props.body.en') #>> '{}' from experience.page_release where page_id = @id and release = 2", ("id", Guid.Parse(id))));
        var live = (await OkAsync(client.GetAsync($"{Pages}/{id}/schedule"))).GetProperty("current");
        Assert.Equal("live", live.GetProperty("status").GetString());
        Assert.Equal(2, live.GetProperty("release").GetInt32());

        Assert.Equal(1, await RunAsync(t, end.AddSeconds(30)));
        Assert.Equal(3, await CurrentAsync(t, id));
        Assert.Equal(1, await world.Database.TenantScalarAsync<int?>(t.Id, "select rollback_of from experience.page_release where page_id = @id and release = 3", ("id", Guid.Parse(id))));
        await world.Database.AssertAuditedAsync(t.Id, AuditActions.PageExpired, id);
        Assert.Equal("expired", (await OkAsync(client.GetAsync($"{Pages}/{id}/schedule"))).GetProperty("current").GetProperty("status").GetString());
        Assert.Equal(0, await RunAsync(t, end.AddDays(1)));
    }

    [Fact]
    public async Task A_release_that_fails_validation_at_go_live_stays_out_and_the_publisher_is_notified()
    {
        var t = await FreshAsync();
        var client = t["publisher"].Client;
        var page = await PageAsync(t, "publisher", t.Unit("Gulf"), "Old text.");
        var id = page["id"]!.GetValue<string>();
        await OkAsync(client.PostAsJsonAsync($"{Pages}/{id}/publish", new { }));
        var at = DateTime.UtcNow.Date.AddDays(3).AddHours(8);
        var view = await OkAsync(client.PutAsJsonAsync($"{Pages}/{id}/schedule", new { goLiveAt = At(at) }));
        var goLive = view.GetProperty("goLiveAt").GetDateTimeOffset();

        // The component the page uses is turned off before the time comes.
        await OkAsync(t["admin"].Client.PutAsJsonAsync("/api/v1/experience/components/reference.alpha.text/enablement", new { enabled = false }));
        Assert.Equal(1, await RunAsync(t, goLive.AddMinutes(1)));
        Assert.Equal(1, await CurrentAsync(t, id));
        var failed = (await OkAsync(client.GetAsync($"{Pages}/{id}/schedule"))).GetProperty("current");
        Assert.Equal("failed", failed.GetProperty("status").GetString());
        Assert.NotEmpty(failed.GetProperty("findings").EnumerateArray());
        await world.Database.AssertAuditedAsync(t.Id, AuditActions.PageScheduleFailed, id);
        Assert.Equal(1, await world.Database.CountInTenantAsync(t.Id,
            "select count(*) from notifications.notification where user_id = @u and template_key = @k", ("u", t["publisher"].Id), ("k", PageSchedules.FailedTemplate)));

        // A failed schedule frees the page for another.
        await OkAsync(t["admin"].Client.PutAsJsonAsync("/api/v1/experience/components/reference.alpha.text/enablement", new { enabled = true }));
        await OkAsync(client.PutAsJsonAsync($"{Pages}/{id}/schedule", new { goLiveAt = At(at.AddDays(1)) }));
    }

    [Fact]
    public async Task Schedules_are_refused_outside_their_rules_and_a_cancelled_or_expiring_first_release_behaves()
    {
        var t = await FreshAsync();
        var client = t["publisher"].Client;
        var page = await PageAsync(t, "publisher", t.Unit("Gulf"), "Launch notes.");
        var id = page["id"]!.GetValue<string>();
        var at = DateTime.UtcNow.Date.AddDays(4).AddHours(10);

        var past = await client.PutAsJsonAsync($"{Pages}/{id}/schedule", new { goLiveAt = At(DateTime.UtcNow.AddDays(-1)) });
        Assert.Equal("validation.page.schedule_time", await KeyAsync(past));
        var missing = await client.PutAsJsonAsync($"{Pages}/{id}/schedule", new { reason = "no time" });
        Assert.Equal("validation.page.schedule_time", await KeyAsync(missing));
        var backwards = await client.PutAsJsonAsync($"{Pages}/{id}/schedule", new { goLiveAt = At(at), expiresAt = At(at.AddHours(-1)) });
        Assert.Equal("validation.page.schedule_expiry", await KeyAsync(backwards));

        // Editors do not publish; a publisher of another unit and another tenant find nothing.
        Assert.Equal(HttpStatusCode.NotFound, (await t["editor"].Client.PutAsJsonAsync($"{Pages}/{id}/schedule", new { goLiveAt = At(at) })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await t["far"].Client.PutAsJsonAsync($"{Pages}/{id}/schedule", new { goLiveAt = At(at) })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await t["far"].Client.GetAsync($"{Pages}/{id}/schedule")).StatusCode);
        var other = await FreshAsync();
        Assert.Equal(HttpStatusCode.NotFound, (await other["admin"].Client.PutAsJsonAsync($"{Pages}/{id}/schedule", new { goLiveAt = At(at) })).StatusCode);
        var none = await OkAsync(client.GetAsync($"{Pages}/{id}/schedule"));
        Assert.Equal(JsonValueKind.Null, none.GetProperty("current").ValueKind);
        Assert.False(string.IsNullOrEmpty(none.GetProperty("timeZone").GetString()));

        // Cancelled, nothing goes live.
        var view = await OkAsync(client.PutAsJsonAsync($"{Pages}/{id}/schedule", new { goLiveAt = At(at) }));
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"{Pages}/{id}/schedule")).StatusCode);
        await world.Database.AssertAuditedAsync(t.Id, AuditActions.PageScheduleCancelled, id);
        Assert.Equal(0, await RunAsync(t, view.GetProperty("goLiveAt").GetDateTimeOffset().AddMinutes(1)));
        Assert.Null(await CurrentAsync(t, id));
        Assert.Equal("conflict.page.not_scheduled", await KeyAsync(await client.DeleteAsync($"{Pages}/{id}/schedule")));

        // A first release with an end: the page goes offline again at the expiry.
        view = await OkAsync(client.PutAsJsonAsync($"{Pages}/{id}/schedule", new { goLiveAt = At(at), expiresAt = At(at.AddHours(6)) }));
        await RunAsync(t, view.GetProperty("goLiveAt").GetDateTimeOffset().AddMinutes(1));
        Assert.Equal(1, await CurrentAsync(t, id));
        await RunAsync(t, view.GetProperty("expiresAt").GetDateTimeOffset().AddMinutes(1));
        Assert.Null(await CurrentAsync(t, id));

        // Under an approval route the page goes live through approval instead.
        await OkAsync(t["admin"].Client.PutAsJsonAsync("/api/v1/experience/page-routes", new { orgUnitId = t.Unit("Gulf"), approverIds = Array.Empty<Guid>() }));
        var routed = await client.PutAsJsonAsync($"{Pages}/{id}/schedule", new { goLiveAt = At(at.AddDays(1)) });
        Assert.Equal("conflict.page.approval_required", await KeyAsync(routed));
    }
}
