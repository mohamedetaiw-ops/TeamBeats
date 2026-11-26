using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TeamBeats.Audit.Contracts;
using TeamBeats.Authorization.Contracts;
using TeamBeats.Messaging;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Testing;
using Xunit;

namespace TeamBeats.Integration.Tests;

/// <summary>
/// Spec 014 R3 US9 and FR-023 (T018): the assistant suggests questions about the item on the page the person is on, only
/// for an item they may read, with no model call; picking one is an ordinary first turn kept to the item. The person can
/// turn suggestions off, the tenant can turn them off for everyone (audited), a package without them offers none, and
/// another tenant's item is never suggested.
/// </summary>
[Collection(WorldCollection.Name)]
public sealed class AssistantSuggestionTests(TenantWorld world)
{
    private const string Suggestions = "/api/v1/assistant/suggestions";
    private const string Preference = "/api/v1/assistant/suggestions/preference";

    private static async Task<JsonElement> OkAsync(Task<HttpResponseMessage> call, HttpStatusCode expected = HttpStatusCode.OK)
    {
        var response = await call;
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == expected, $"{(int)response.StatusCode}: {text}");
        return text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone();
    }

    private static async Task<string> ProblemKeyAsync(Task<HttpResponseMessage> call)
    {
        var response = await call;
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.BadRequest, $"{(int)response.StatusCode}: {text}");
        return JsonDocument.Parse(text).RootElement.GetProperty("errors").EnumerateObject().First().Value[0].GetProperty("key").GetString()!;
    }

    private sealed record World(TestTenant T, Guid Open, Guid Closed);

    private static object Body(string text) => new { blocks = new object[] { new { type = "paragraph", children = new object[] { new { text } } } } };

    private static async Task<Guid> NewsAsync(HttpClient client, string title, string body, string? classification = null)
    {
        var id = (await OkAsync(client.PostAsJsonAsync("/api/v1/content/items",
            new { type = "news", classification, variants = new Dictionary<string, object> { ["en"] = new { title, body = Body(body) } } }), HttpStatusCode.Created))
            .GetProperty("id").GetGuid();
        await OkAsync(client.PostAsJsonAsync($"/api/v1/content/items/{id}/publish", new { }));
        return id;
    }

    private async Task<World> FreshAsync(string package = "workplace_plus")
    {
        var t = await world.CreateTenantAsync("as-" + Guid.NewGuid().ToString("N")[..8], package);
        await world.SeedUserAsync(t, "pia", MembershipTypes.Employee);
        await world.SeedUserAsync(t, "emp", MembershipTypes.Employee);
        await world.AssignAsync(t, "pia", BuiltInRoles.ContentPublisher, "tenant", null);
        var open = await NewsAsync(t["pia"].Client, "Canteen opening hours", "The canteen opens at seven and closes at four on weekdays.");
        var closed = await NewsAsync(t["pia"].Client, "Board budget", "Confidential numbers for the board.", "confidential");
        var dispatcher = world.Factory.Services.GetRequiredService<OutboxDispatcher>();
        for (var i = 0; i < 20 && await dispatcher.DispatchBatchAsync(500, CancellationToken.None) > 0; i++)
        {
        }
        return new World(t, open, closed);
    }

    private static Task<JsonElement> ForAsync(HttpClient client, string path) => OkAsync(client.GetAsync($"{Suggestions}?path={Uri.EscapeDataString(path)}"));

    private static JsonElement[] List(JsonElement view) => [.. view.GetProperty("suggestions").EnumerateArray()];

    [Fact]
    public async Task Suggestions_are_offered_only_for_an_item_the_person_may_read_and_a_picked_one_is_kept_to_it()
    {
        var w = await FreshAsync();
        var emp = w.T["emp"].Client;

        var view = await ForAsync(emp, $"/content/read/{w.Open}");
        Assert.True(view.GetProperty("available").GetBoolean());
        Assert.True(view.GetProperty("on").GetBoolean());
        var list = List(view);
        Assert.Equal(["summarize", "key_points"], list.Select(s => s.GetProperty("key").GetString()));
        Assert.All(list, s =>
        {
            Assert.Equal("Canteen opening hours", s.GetProperty("title").GetString());
            Assert.Equal("content", s.GetProperty("scope").GetProperty("source").GetString());
            Assert.Equal(w.Open, s.GetProperty("scope").GetProperty("itemId").GetGuid());
        });

        // Showing them is an outcome for the usefulness report of spec 021 (FR-024).
        var dispatcher = world.Factory.Services.GetRequiredService<OutboxDispatcher>();
        for (var i = 0; i < 20 && await dispatcher.DispatchBatchAsync(500, CancellationToken.None) > 0; i++)
        {
        }
        Assert.True(await world.Database.TenantScalarAsync<long>(w.T.Id,
            "select count(*) from analytics.event where name = 'recommendation.outcome' and fields->>'surface' = 'assistant' and fields->>'outcome' = 'shown'") >= 1);

        // Nothing for an item they may not read, one that does not exist, or a page that shows no item.
        Assert.Empty(List(await ForAsync(emp, $"/content/read/{w.Closed}")));
        Assert.Empty(List(await ForAsync(emp, $"/content/read/{Guid.NewGuid()}")));
        Assert.Empty(List(await ForAsync(emp, "/")));
        Assert.Empty(List(await ForAsync(emp, $"/content/read/{w.Open}/../../admin")));
        // The publisher may read the confidential one, but the AI policy keeps it from the assistant, so nothing is offered.
        Assert.Empty(List(await ForAsync(w.T["pia"].Client, $"/content/read/{w.Closed}")));

        // Picking a suggestion is a first turn kept to the item: anything cited comes from it.
        var turn = await OkAsync(emp.PostAsJsonAsync("/api/v1/assistant/turns",
            new { question = "Summarize this article", scope = new { source = "content", itemId = w.Open } }));
        Assert.All(turn.GetProperty("citations").EnumerateArray(), c => Assert.Equal(w.Open, c.GetProperty("itemId").GetGuid()));
        Assert.Equal(HttpStatusCode.NotFound, (await emp.PostAsJsonAsync("/api/v1/assistant/turns",
            new { question = "Summarize this article", scope = new { source = "content", itemId = w.Closed } })).StatusCode);

        // Another tenant never sees this tenant's item.
        var other = await FreshAsync();
        Assert.Empty(List(await ForAsync(other.T["emp"].Client, $"/content/read/{w.Open}")));
    }

    [Fact]
    public async Task The_person_and_the_tenant_can_turn_suggestions_off_and_a_package_without_them_offers_none()
    {
        var w = await FreshAsync();
        var emp = w.T["emp"].Client;
        var path = $"/content/read/{w.Open}";

        Assert.Equal("validation.assistant.suggestions_choice", await ProblemKeyAsync(emp.PutAsJsonAsync(Preference, new { })));
        Assert.False((await OkAsync(emp.PutAsJsonAsync(Preference, new { on = false }))).GetProperty("on").GetBoolean());
        var off = await ForAsync(emp, path);
        Assert.True(off.GetProperty("available").GetBoolean());
        Assert.False(off.GetProperty("on").GetBoolean());
        Assert.Empty(List(off));
        // Only their own choice: someone else still gets them.
        Assert.Equal(2, List(await ForAsync(w.T["pia"].Client, path)).Length);
        await OkAsync(emp.PutAsJsonAsync(Preference, new { on = true }));
        Assert.Equal(2, List(await ForAsync(emp, path)).Length);

        // The tenant turns them off for everyone; only the AI policy administrator may, and it is audited.
        Assert.Equal(HttpStatusCode.Forbidden, (await emp.PutAsJsonAsync("/api/v1/assistant/settings", new { suggestionsEnabled = false })).StatusCode);
        var settings = await OkAsync(w.T["admin"].Client.PutAsJsonAsync("/api/v1/assistant/settings", new { suggestionsEnabled = false }));
        Assert.False(settings.GetProperty("suggestionsEnabled").GetBoolean());
        await world.Database.AssertAuditedAsync(w.T.Id, AuditActions.AssistantSuggestionsChanged, "suggestions");
        var tenantOff = await ForAsync(emp, path);
        Assert.False(tenantOff.GetProperty("available").GetBoolean());
        Assert.Empty(List(tenantOff));
        Assert.Equal("validation.assistant.suggestions_unavailable", await ProblemKeyAsync(emp.PutAsJsonAsync(Preference, new { on = true })));

        // A package without suggestions offers none.
        var plain = await FreshAsync("workplace");
        var none = await ForAsync(plain.T["emp"].Client, $"/content/read/{plain.Open}");
        Assert.False(none.GetProperty("available").GetBoolean());
        Assert.Empty(List(none));
    }

    [Fact]
    public void Only_pages_that_show_one_item_are_recognised()
    {
        var id = Guid.NewGuid();
        Assert.Equal(("content", id), Pick($"/content/read/{id}"));
        Assert.Equal(("documents", id), Pick($"/documents/{id}"));
        Assert.Equal(("policies", id), Pick($"/policies/{id}/"));
        Assert.Null(Pick($"/documents/libraries/{id}"));
        Assert.Null(Pick($"/policies/{id}/edit"));
        Assert.Null(Pick("/content/read/not-an-id"));
        Assert.Null(Pick(null));
    }

    private static (string, Guid)? Pick(string? path) =>
        TeamBeats.Modules.Agents.AssistantSuggestions.Parse(path) is { } p ? (p.Source, p.ItemId) : null;
}
