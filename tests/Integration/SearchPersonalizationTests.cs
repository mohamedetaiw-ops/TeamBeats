using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TeamBeats.Authorization.Contracts;
using TeamBeats.Messaging;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Testing;
using Xunit;

namespace TeamBeats.Integration.Tests;

/// <summary>
/// Spec 012 R3 US9 and FR-023 (T017): results the person opened most move up and are marked, only while spec 008
/// behavioral personalization applies to them; the same items, total and facets come back either way; best bets stay
/// first; another person's use and the public API never change the order.
/// </summary>
[Collection(WorldCollection.Name)]
public sealed class SearchPersonalizationTests(TenantWorld world)
{
    private const string Policy = "/api/v1/experience/behavior-policy";
    private const string Choice = "/api/v1/me/personalization/behavior";

    private static async Task<JsonElement> OkAsync(Task<HttpResponseMessage> call, HttpStatusCode expected = HttpStatusCode.OK)
    {
        var response = await call;
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == expected, $"{(int)response.StatusCode}: {text}");
        return text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone();
    }

    private sealed record World(TestTenant T, Guid Strong, Guid Weak, string Word);

    private static object Body(string text) => new { blocks = new object[] { new { type = "paragraph", children = new object[] { new { text } } } } };

    private static async Task<Guid> NewsAsync(HttpClient client, string title, string body)
    {
        var id = (await OkAsync(client.PostAsJsonAsync("/api/v1/content/items",
            new { type = "news", variants = new Dictionary<string, object> { ["en"] = new { title, body = Body(body) } } }), HttpStatusCode.Created)).GetProperty("id").GetGuid();
        await OkAsync(client.PostAsJsonAsync($"/api/v1/content/items/{id}/publish", new { }));
        return id;
    }

    /// <summary>Two news items that match one word; the first matches it more often, so it ranks first by default.</summary>
    private async Task<World> FreshAsync(string package = "enterprise_gov")
    {
        var t = await world.CreateTenantAsync("sp-" + Guid.NewGuid().ToString("N")[..8], package);
        foreach (var name in new[] { "pia", "employee", "other" })
        {
            await world.SeedUserAsync(t, name, MembershipTypes.Employee);
        }
        await world.AssignAsync(t, "pia", BuiltInRoles.ContentPublisher, "tenant", null);
        var word = "zq" + Guid.NewGuid().ToString("N")[..6];
        var strong = await NewsAsync(t["pia"].Client, $"{word} {word} handbook", $"{word} {word} {word} everything about it.");
        var weak = await NewsAsync(t["pia"].Client, $"Notes on the {word} rollout", "A short note.");
        var dispatcher = world.Factory.Services.GetRequiredService<OutboxDispatcher>();
        for (var i = 0; i < 20 && await dispatcher.DispatchBatchAsync(500, CancellationToken.None) > 0; i++)
        {
        }
        return new World(t, strong, weak, word);
    }

    private static async Task VisitAsync(HttpClient client, Guid item, int times)
    {
        for (var i = 0; i < times; i++)
        {
            Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsJsonAsync("/api/v1/me/recents", new { path = $"/content/read/{item}", label = "x" })).StatusCode);
        }
    }

    private static Task<JsonElement> SearchAsync(HttpClient client, string q) => OkAsync(client.GetAsync($"/api/v1/search?q={q}"));

    private static Guid[] Ids(JsonElement page) => [.. page.GetProperty("results").EnumerateArray().Select(r => r.GetProperty("itemId").GetGuid())];

    private static bool[] Marks(JsonElement page) => [.. page.GetProperty("results").EnumerateArray().Select(r => r.GetProperty("personalized").GetBoolean())];

    [Fact]
    public async Task What_the_person_opened_most_moves_up_and_is_marked_only_while_personalization_applies_to_them()
    {
        var w = await FreshAsync();
        var employee = w.T["employee"].Client;

        // Off: the default order, nothing marked, and visits are not counted.
        await VisitAsync(employee, w.Weak, 3);
        var before = await SearchAsync(employee, w.Word);
        Assert.Equal([w.Strong, w.Weak], Ids(before));
        Assert.Equal([false, false], Marks(before));

        // On with opt-in and the person agreed: their most opened item goes first and says why.
        await OkAsync(w.T["admin"].Client.PutAsJsonAsync(Policy, new { enabled = true, consentMode = "opt_in" }));
        Assert.Equal([w.Strong, w.Weak], Ids(await SearchAsync(employee, w.Word)));
        await OkAsync(employee.PutAsJsonAsync(Choice, new { consented = true }));
        await VisitAsync(employee, w.Weak, 3);
        var after = await SearchAsync(employee, w.Word);
        Assert.Equal([w.Weak, w.Strong], Ids(after));
        Assert.Equal([true, false], Marks(after));
        // The same items in number and facets: ranking never adds or hides anything.
        Assert.Equal(before.GetProperty("total").GetInt32(), after.GetProperty("total").GetInt32());
        Assert.Equal(before.GetProperty("facets").GetRawText(), after.GetProperty("facets").GetRawText());

        // Another person's use never ranks for them.
        var other = await SearchAsync(w.T["other"].Client, w.Word);
        Assert.Equal([w.Strong, w.Weak], Ids(other));
        Assert.Equal([false, false], Marks(other));

        // Declining deletes the signals, so the default order is back.
        await OkAsync(employee.PutAsJsonAsync(Choice, new { consented = false }));
        var declined = await SearchAsync(employee, w.Word);
        Assert.Equal([w.Strong, w.Weak], Ids(declined));
        Assert.Equal([false, false], Marks(declined));
    }

    [Fact]
    public async Task Best_bets_stay_first_and_a_package_without_personalization_ranks_as_before()
    {
        var w = await FreshAsync();
        var employee = w.T["employee"].Client;
        await OkAsync(w.T["admin"].Client.PutAsJsonAsync(Policy, new { enabled = true, consentMode = "opt_out" }));
        await VisitAsync(employee, w.Weak, 5);
        Assert.Equal([w.Weak, w.Strong], Ids(await SearchAsync(employee, w.Word)));

        // A best bet for the query keeps its place above anything the person used.
        await OkAsync(w.T["admin"].Client.PostAsJsonAsync("/api/v1/search/best-bets",
            new { query = w.Word, source = "content", itemId = w.Strong }), HttpStatusCode.Created);
        var bet = await SearchAsync(employee, w.Word);
        Assert.Equal([w.Strong, w.Weak], Ids(bet));
        Assert.True(bet.GetProperty("results")[0].GetProperty("recommended").GetBoolean());
        Assert.Equal([false, true], Marks(bet));

        // A package without behavioral personalization: the same use changes nothing.
        var plain = await FreshAsync("workplace_plus");
        await VisitAsync(plain.T["employee"].Client, plain.Weak, 5);
        var page = await SearchAsync(plain.T["employee"].Client, plain.Word);
        Assert.Equal([plain.Strong, plain.Weak], Ids(page));
        Assert.Equal([false, false], Marks(page));
    }
}
