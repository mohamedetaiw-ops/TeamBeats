using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TeamBeats.Authorization.Contracts;
using TeamBeats.Entitlements.Contracts;
using TeamBeats.Messaging;
using TeamBeats.Modules.Search;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Testing;
using Xunit;

namespace TeamBeats.Integration.Tests;

/// <summary>
/// Spec 012 US7 (R2, FR-019): a person saves searches and gets at most one alert a day per saved search, through the
/// notification service, counting only new items that match and that they may read now. Each person sees and changes only
/// their own saved searches; other tenants see none; the capability turns the feature off.
/// </summary>
[Collection(WorldCollection.Name)]
public sealed class SavedSearchTests(TenantWorld world)
{
    private const string Saved = "/api/v1/search/saved";
    private const string Items = "/api/v1/content/items";

    private static async Task<JsonElement> OkAsync(Task<HttpResponseMessage> call, HttpStatusCode expected = HttpStatusCode.OK)
    {
        var response = await call;
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == expected, $"{(int)response.StatusCode}: {text}");
        return text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone();
    }

    private static async Task<string> ProblemKeyAsync(Task<HttpResponseMessage> call, HttpStatusCode expected)
    {
        var response = await call;
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == expected, $"{(int)response.StatusCode}: {text}");
        var body = JsonDocument.Parse(text).RootElement;
        return body.TryGetProperty("detailKey", out var detail) ? detail.GetString()! : body.GetProperty("code").GetString()!;
    }

    private static Guid Id(JsonElement e) => e.GetProperty("id").GetGuid();

    private async Task DispatchAsync()
    {
        var dispatcher = world.Factory.Services.GetRequiredService<OutboxDispatcher>();
        for (var i = 0; i < 20 && await dispatcher.DispatchBatchAsync(500, CancellationToken.None) > 0; i++)
        {
        }
    }

    private static object Body(string text) => new { blocks = new object[] { new { type = "paragraph", children = new object[] { new { text } } } } };

    private static async Task<Guid> NewsAsync(HttpClient client, string title, string body, string? classification = null)
    {
        var id = Id(await OkAsync(client.PostAsJsonAsync(Items, new { type = "news", classification, variants = new { en = new { title, body = Body(body) } } }),
            HttpStatusCode.Created));
        await OkAsync(client.PostAsJsonAsync($"{Items}/{id}/publish", new { }));
        return id;
    }

    /// <summary>Runs the 15 minute scheduler once for the tenant, then delivers the checks it asked for.</summary>
    private async Task<int> RunAlertsAsync(TestTenant t)
    {
        var due = await world.Factory.InScopeAsync(t.Id, async sp =>
        {
            var count = await sp.GetRequiredService<SavedSearchScheduler>().RunDueAsync(CancellationToken.None);
            await sp.GetRequiredService<IDbSession>().CommitAsync(CancellationToken.None);
            return count;
        });
        await DispatchAsync();
        return due;
    }

    /// <summary>Moves a saved search's last check a day and an hour back, as if a day had passed.</summary>
    private async Task DayPassesAsync(TestTenant t, Guid saved) =>
        Assert.Equal(1L, await ExecAsync(t, "update search.saved_search set checked_at = checked_at - interval '25 hours' where id = @id returning 1", saved));

    /// <summary>Changes rows as the owner inside the tenant (row level security is forced) and counts them.</summary>
    private Task<long> ExecAsync(TestTenant t, string update, Guid id) =>
        world.Database.TenantScalarAsync<long>(t.Id, $"with changed as ({update}) select count(*) from changed", ("id", id));

    private Task<long> AlertsAsync(TestTenant t, string user) =>
        world.Database.CountInTenantAsync(t.Id,
            "select count(*) from notifications.notification where user_id = @u and template_key = 'search.saved_alert'", ("u", t[user].Id));

    [Fact]
    public async Task A_saved_search_alerts_once_a_day_about_new_items_the_person_may_read()
    {
        var t = await world.CreateTenantAsync("ss-" + Guid.NewGuid().ToString("N")[..8], "workplace_plus");
        foreach (var name in new[] { "pia", "emp", "ed" })
        {
            await world.SeedUserAsync(t, name, MembershipTypes.Employee);
        }
        await world.AssignAsync(t, "pia", BuiltInRoles.ContentPublisher, "tenant", null);
        await world.AssignAsync(t, "ed", BuiltInRoles.ContentEditor, "tenant", null);
        var emp = t["emp"].Client;

        // An item indexed before the search was saved never counts as new.
        var old = await NewsAsync(t["pia"].Client, "Forklift rota", "The old forklift rota.");
        await DispatchAsync();
        Assert.Equal(1L, await ExecAsync(t, "update search.item_state set changed_at = changed_at - interval '2 days' where item_id = @id returning 1", old));
        Assert.True(await ExecAsync(t, "update search.unit set updated_at = updated_at - interval '2 days' where item_id = @id returning 1", old) > 0);

        var saved = await OkAsync(emp.PostAsJsonAsync(Saved, new { query = "forklift" }), HttpStatusCode.Created);
        Assert.True(saved.GetProperty("alerts").GetBoolean());
        Assert.Equal("forklift", saved.GetProperty("query").GetString());
        var editorSaved = Id(await OkAsync(t["ed"].Client.PostAsJsonAsync(Saved, new { query = "forklift" }), HttpStatusCode.Created));
        Assert.Equal("conflict.search.saved_exists", await ProblemKeyAsync(emp.PostAsJsonAsync(Saved, new { query = "forklift" }), HttpStatusCode.Conflict));
        Assert.Equal(HttpStatusCode.BadRequest, (await emp.PostAsJsonAsync(Saved, new { query = " " })).StatusCode);

        // New items: one open, one confidential the employee cannot read, one that does not match.
        await NewsAsync(t["pia"].Client, "Forklift safety week", "Every forklift gets a brake check.");
        await NewsAsync(t["pia"].Client, "Forklift budget", "Confidential forklift numbers.", "confidential");
        await NewsAsync(t["pia"].Client, "Summer leave", "Plan your leave early.");
        await DispatchAsync();

        // Nothing is due until a day has passed since the last check.
        Assert.Equal(0, await RunAlertsAsync(t));
        Assert.Equal(0, await AlertsAsync(t, "emp"));

        await DayPassesAsync(t, Id(saved));
        await DayPassesAsync(t, editorSaved);
        Assert.Equal(2, await RunAlertsAsync(t));
        // The employee hears of the one item they may read; the editor, who reads confidential news, of both.
        Assert.Equal(1, await AlertsAsync(t, "emp"));
        Assert.Equal("1", await world.Database.TenantScalarAsync<string>(t.Id,
            "select parameters->>'count' from notifications.notification where user_id = @u and template_key = 'search.saved_alert'", ("u", t["emp"].Id)));
        Assert.Equal("/search?q=forklift", await world.Database.TenantScalarAsync<string>(t.Id,
            "select link from notifications.notification where user_id = @u and template_key = 'search.saved_alert'", ("u", t["emp"].Id)));
        Assert.Equal("search_alerts", await world.Database.TenantScalarAsync<string>(t.Id,
            "select category from notifications.notification where user_id = @u and template_key = 'search.saved_alert'", ("u", t["emp"].Id)));
        Assert.Equal("2", await world.Database.TenantScalarAsync<string>(t.Id,
            "select parameters->>'count' from notifications.notification where user_id = @u and template_key = 'search.saved_alert'", ("u", t["ed"].Id)));
        Assert.NotEqual(JsonValueKind.Null, (await OkAsync(emp.GetAsync(Saved)))[0].GetProperty("alertedAt").ValueKind);

        // Not again the same day, and nothing when no new item came in since the last check.
        Assert.Equal(0, await RunAlertsAsync(t));
        await DayPassesAsync(t, Id(saved));
        Assert.Equal(1, await RunAlertsAsync(t));
        Assert.Equal(1, await AlertsAsync(t, "emp"));

        // Alerts off: never checked. The editor cannot see or change the employee's saved search.
        await OkAsync(emp.PutAsJsonAsync($"{Saved}/{Id(saved)}/alerts", new { alerts = false }));
        await DayPassesAsync(t, Id(saved));
        Assert.Equal(0, await RunAlertsAsync(t));
        Assert.Single((await OkAsync(t["ed"].Client.GetAsync(Saved))).EnumerateArray());
        Assert.Equal(HttpStatusCode.NotFound, (await t["ed"].Client.PutAsJsonAsync($"{Saved}/{Id(saved)}/alerts", new { alerts = true })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await t["ed"].Client.DeleteAsync($"{Saved}/{Id(saved)}")).StatusCode);

        // Another tenant sees none of these and cannot touch them.
        var other = await world.CreateTenantAsync("ss-x-" + Guid.NewGuid().ToString("N")[..6], "workplace_plus");
        Assert.Empty((await OkAsync(other["admin"].Client.GetAsync(Saved))).EnumerateArray());
        Assert.Equal(HttpStatusCode.NotFound, (await other["admin"].Client.DeleteAsync($"{Saved}/{Id(saved)}")).StatusCode);

        await OkAsync(emp.DeleteAsync($"{Saved}/{Id(saved)}"), HttpStatusCode.NoContent);
        Assert.Empty((await OkAsync(emp.GetAsync(Saved))).EnumerateArray());
    }

    [Fact]
    public async Task Saved_searches_are_limited_per_person_and_follow_the_capability()
    {
        var t = await world.CreateTenantAsync("ss-l-" + Guid.NewGuid().ToString("N")[..6], "workplace_plus");
        await world.SeedUserAsync(t, "emp", MembershipTypes.Employee);
        var emp = t["emp"].Client;
        for (var i = 0; i < SavedSearches.MaxPerPerson; i++)
        {
            await OkAsync(emp.PostAsJsonAsync(Saved, new { query = $"topic {i}", alerts = false }), HttpStatusCode.Created);
        }
        Assert.Equal("conflict.search.saved_limit", await ProblemKeyAsync(emp.PostAsJsonAsync(Saved, new { query = "one more" }), HttpStatusCode.Conflict));

        // With the capability off the endpoints refuse, and a search saved with alerts stops alerting.
        var alerting = Id((await OkAsync(emp.GetAsync(Saved)))[0]);
        await OkAsync(emp.PutAsJsonAsync($"{Saved}/{alerting}/alerts", new { alerts = true }));
        await DayPassesAsync(t, alerting);
        await world.SetCapabilityAsync(t, Capabilities.SearchSavedAlerts, new { enabled = false });
        Assert.Equal(HttpStatusCode.Forbidden, (await emp.GetAsync(Saved)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await emp.PostAsJsonAsync(Saved, new { query = "anything" })).StatusCode);
        Assert.Equal(1, await RunAlertsAsync(t));
        Assert.False(await world.Database.TenantScalarAsync<bool>(t.Id, "select alerts from search.saved_search where id = @id", ("id", alerting)));
    }
}
