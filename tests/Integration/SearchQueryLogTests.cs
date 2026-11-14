using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TeamBeats.Audit.Contracts;
using TeamBeats.Authorization.Contracts;
using TeamBeats.Messaging;
using TeamBeats.Modules.Search.Contracts;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Testing;
using Xunit;

namespace TeamBeats.Integration.Tests;

/// <summary>
/// Spec 012 T016 (FR-013, US8 scenario 2): every first page of results is a pseudonymous query event with the first
/// click on a result, and the feed for the spec 021 search dashboard lists top, zero result and no click queries only
/// when five or more people searched for them, never queries that look like personal data, and no query text at all
/// once the tenant turned it off.
/// </summary>
[Collection(WorldCollection.Name)]
public sealed class SearchQueryLogTests(TenantWorld world)
{
    private const string Search = "/api/v1/search";

    private static async Task<JsonElement> OkAsync(Task<HttpResponseMessage> call, HttpStatusCode expected = HttpStatusCode.OK)
    {
        var response = await call;
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == expected, $"{(int)response.StatusCode}: {text}");
        return text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone();
    }

    private async Task DispatchAsync()
    {
        var dispatcher = world.Factory.Services.GetRequiredService<OutboxDispatcher>();
        for (var i = 0; i < 20 && await dispatcher.DispatchBatchAsync(500, CancellationToken.None) > 0; i++)
        {
        }
    }

    private static object Body(string text) => new { blocks = new object[] { new { type = "paragraph", children = new object[] { new { text } } } } };

    private static Task<JsonElement> SearchAsync(HttpClient client, string q) => OkAsync(client.GetAsync($"{Search}?q={Uri.EscapeDataString(q)}"));

    private Task<SearchQueryReport> ReportAsync(TestTenant t, Guid user) =>
        world.Factory.AsUserAsync(t.Id, user, sp => sp.GetRequiredService<ISearchAnalyticsFeed>()
            .ReportAsync(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddMinutes(1), null));

    [Fact]
    public async Task The_query_feed_counts_searches_and_clicks_and_shows_only_queries_five_people_share()
    {
        var t = await world.CreateTenantAsync("sq-" + Guid.NewGuid().ToString("N")[..8], "workplace_plus");
        var people = Enumerable.Range(1, 6).Select(i => $"p{i}").ToList();
        foreach (var name in people.Append("pia"))
        {
            await world.SeedUserAsync(t, name, MembershipTypes.Employee);
        }
        await world.AssignAsync(t, "pia", BuiltInRoles.ContentPublisher, "tenant", null);
        var news = (await OkAsync(t["pia"].Client.PostAsJsonAsync("/api/v1/content/items",
            new { type = "news", variants = new { en = new { title = "Forklift safety week", body = Body("Every forklift gets a brake check.") } } }), HttpStatusCode.Created))
            .GetProperty("id").GetGuid();
        await OkAsync(t["pia"].Client.PostAsJsonAsync($"/api/v1/content/items/{news}/publish", new { }));
        await DispatchAsync();

        // Six people search for forklifts, five for something that is not there, four for the canteen, five for an email address.
        var first = await SearchAsync(t["p1"].Client, "Forklift");
        var queryId = first.GetProperty("queryId").GetGuid();
        foreach (var p in people.Skip(1))
        {
            await SearchAsync(t[p].Client, "forklift");
        }
        foreach (var p in people.Take(5))
        {
            await SearchAsync(t[p].Client, "zebra quantum");
            await SearchAsync(t[p].Client, "laila@example.test");
        }
        foreach (var p in people.Take(4))
        {
            await SearchAsync(t[p].Client, "canteen");
        }
        // A second page is not another search.
        Assert.Equal(JsonValueKind.Null, (await OkAsync(t["p1"].Client.GetAsync($"{Search}?q=forklift&page=2"))).GetProperty("queryId").ValueKind);

        // The first click counts once, only from the person who searched.
        await OkAsync(t["p2"].Client.PostAsJsonAsync($"{Search}/clicks", new { queryId, position = 1 }), HttpStatusCode.NoContent);
        await OkAsync(t["p1"].Client.PostAsJsonAsync($"{Search}/clicks", new { queryId, position = 1 }), HttpStatusCode.NoContent);
        await OkAsync(t["p1"].Client.PostAsJsonAsync($"{Search}/clicks", new { queryId, position = 3 }), HttpStatusCode.NoContent);
        await OkAsync(t["p1"].Client.PostAsJsonAsync($"{Search}/clicks", new { queryId, position = 0 }), HttpStatusCode.BadRequest);
        Assert.Equal(1, await world.Database.TenantScalarAsync<int>(t.Id, "select click_position from search.query_event where id = @id", ("id", queryId)));
        // Events carry the analytics key, never the user ID.
        Assert.Equal(0, await world.Database.TenantScalarAsync<long>(t.Id, "select count(*) from search.query_event where person = @u", ("u", t["p1"].Id.ToString())));

        var report = await ReportAsync(t, t["pia"].Id);
        Assert.Equal(20, report.Searches);
        Assert.Equal(1, report.Clicked);
        Assert.False(report.QueryTextHidden);
        var top = Assert.Single(report.TopQueries, q => q.Query == "forklift");
        Assert.Equal(6, top.Searches);
        Assert.Equal(6, top.People);
        Assert.Equal(1, top.Clicks);
        Assert.Equal("zebra quantum", Assert.Single(report.ZeroResultQueries).Query);
        Assert.DoesNotContain(report.TopQueries, q => q.Query.Contains("canteen", StringComparison.Ordinal) || q.Query.Contains('@'));
        Assert.DoesNotContain(report.NoClickQueries, q => q.Query == "forklift");

        // Only search administrators turn query text off; it clears the words kept, and new searches keep none.
        await OkAsync(t["p1"].Client.PutAsJsonAsync($"{Search}/query-text", new { enabled = false }), HttpStatusCode.Forbidden);
        Assert.True((await OkAsync(t["admin"].Client.GetAsync($"{Search}/query-text"))).GetProperty("enabled").GetBoolean());
        await OkAsync(t["admin"].Client.PutAsJsonAsync($"{Search}/query-text", new { enabled = false }));
        await world.Database.AssertAuditedAsync(t.Id, AuditActions.SearchQueryTextChanged, "query_text");
        await SearchAsync(t["p3"].Client, "forklift");
        Assert.Equal(0, await world.Database.TenantScalarAsync<long>(t.Id, "select count(*) from search.query_event where query_key is not null"));
        var hidden = await ReportAsync(t, t["pia"].Id);
        Assert.True(hidden.QueryTextHidden);
        Assert.Equal(21, hidden.Searches);
        Assert.Empty(hidden.TopQueries);
        Assert.Empty(hidden.ZeroResultQueries);

        // Another tenant has no events of this one.
        var other = await world.CreateTenantAsync("sq-x-" + Guid.NewGuid().ToString("N")[..6], "workplace_plus");
        Assert.Equal(0, (await ReportAsync(other, other["admin"].Id)).Searches);
        await OkAsync(other["admin"].Client.PostAsJsonAsync($"{Search}/clicks", new { queryId, position = 2 }), HttpStatusCode.NoContent);
        Assert.Equal(1, await world.Database.TenantScalarAsync<int>(t.Id, "select click_position from search.query_event where id = @id", ("id", queryId)));
    }
}
