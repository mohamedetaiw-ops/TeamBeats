using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TeamBeats.Audit.Contracts;
using TeamBeats.Authorization.Contracts;
using TeamBeats.Messaging;
using TeamBeats.Modules.AiGateway.Contracts;
using TeamBeats.Modules.Search.Contracts;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Primitives;
using TeamBeats.Testing;
using Xunit;

namespace TeamBeats.Integration.Tests;

/// <summary>
/// Spec 012 slice 2: best bets and synonyms (US4, FR-010) with <c>search.manage</c> and audit, and the retrieval
/// contract for the assistant (US3 scenarios 1 and 2, FR-007): passages with citation anchors, only what the person may
/// read, with passages above the AI classification ceiling withheld and counted; other tenants see nothing.
/// </summary>
[Collection(WorldCollection.Name)]
public sealed class SearchTuningTests(TenantWorld world)
{
    private const string Search = "/api/v1/search";
    private const string Items = "/api/v1/content/items";

    private async Task<TestTenant> FreshAsync()
    {
        var t = await world.CreateTenantAsync("st-" + Guid.NewGuid().ToString("N")[..8], "workplace_plus", "en", ["en", "ar"]);
        foreach (var name in new[] { "pia", "ed", "emp" })
        {
            await world.SeedUserAsync(t, name, MembershipTypes.Employee);
        }
        await world.AssignAsync(t, "pia", BuiltInRoles.ContentPublisher, "tenant", null);
        await world.AssignAsync(t, "ed", BuiltInRoles.ContentEditor, "tenant", null);
        return t;
    }

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
        if (body.TryGetProperty("errors", out var errors))
        {
            return errors.EnumerateObject().First().Value[0].GetProperty("key").GetString()!;
        }
        return body.TryGetProperty("detailKey", out var detail) && detail.ValueKind == JsonValueKind.String ? detail.GetString()! : body.GetProperty("code").GetString()!;
    }

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
        var id = (await OkAsync(client.PostAsJsonAsync(Items, new
        {
            type = "news", classification, variants = new Dictionary<string, object> { ["en"] = new { title, body = Body(body) } },
        }), HttpStatusCode.Created)).GetProperty("id").GetGuid();
        await OkAsync(client.PostAsJsonAsync($"{Items}/{id}/publish", new { }));
        return id;
    }

    private static async Task<JsonElement[]> ResultsAsync(HttpClient client, string q) =>
        [.. (await OkAsync(client.GetAsync($"{Search}?q={Uri.EscapeDataString(q)}"))).GetProperty("results").EnumerateArray()];

    private static Guid[] Ids(JsonElement[] results) => [.. results.Select(r => r.GetProperty("itemId").GetGuid())];

    [Fact]
    public async Task A_best_bet_shows_first_and_marked_for_people_who_may_read_its_item()
    {
        var t = await FreshAsync();
        var letter = await NewsAsync(t["pia"].Client, "Salary letters now in two days", "Ask HR for a salary letter through the services page.");
        var payroll = await NewsAsync(t["pia"].Client, "Payslip dates for December", "Payslips arrive on the 24th this month.");
        var board = await NewsAsync(t["pia"].Client, "Board pay review", "Confidential notes on executive pay.", "confidential");
        await DispatchAsync();

        // Only search administrators manage best bets.
        Assert.Equal(ProblemCodes.AuthzDenied, await ProblemKeyAsync(t["emp"].Client.PostAsJsonAsync($"{Search}/best-bets",
            new { query = "payslip", source = "content", itemId = letter }), HttpStatusCode.Forbidden));
        Assert.Equal(ProblemCodes.AuthzDenied, await ProblemKeyAsync(t["emp"].Client.GetAsync($"{Search}/best-bets"), HttpStatusCode.Forbidden));
        Assert.Equal("validation.search.best_bet.target", await ProblemKeyAsync(t["admin"].Client.PostAsJsonAsync($"{Search}/best-bets",
            new { query = "payslip", source = "content", itemId = Guid.NewGuid() }), HttpStatusCode.BadRequest));
        Assert.Equal("validation.search.best_bet.query", await ProblemKeyAsync(t["admin"].Client.PostAsJsonAsync($"{Search}/best-bets",
            new { query = "  !! ", source = "content", itemId = letter }), HttpStatusCode.BadRequest));
        Assert.Equal("validation.search.locale", await ProblemKeyAsync(t["admin"].Client.PostAsJsonAsync($"{Search}/best-bets",
            new { query = "payslip", locale = "fr", source = "content", itemId = letter }), HttpStatusCode.BadRequest));

        // The query is stored as the search box reads it, so "PAYSLIP!" and "payslip" are the same best bet.
        var bet = await OkAsync(t["admin"].Client.PostAsJsonAsync($"{Search}/best-bets", new { query = "PAYSLIP!", source = "content", itemId = letter }),
            HttpStatusCode.Created);
        Assert.Equal("payslip", bet.GetProperty("query").GetString());
        Assert.Equal("Salary letters now in two days", bet.GetProperty("title").GetString());
        Assert.Equal("validation.search.best_bet.duplicate", await ProblemKeyAsync(t["admin"].Client.PostAsJsonAsync($"{Search}/best-bets",
            new { query = "payslip", source = "content", itemId = letter }), HttpStatusCode.Conflict));
        await OkAsync(t["admin"].Client.PostAsJsonAsync($"{Search}/best-bets", new { query = "payslip", source = "content", itemId = board }), HttpStatusCode.Created);
        await world.Database.AssertAuditedAsync(t.Id, AuditActions.SearchBestBetChanged);

        // The employee gets the letter first, marked, then the item that matches the words; the confidential best bet
        // stays out of the results and the total.
        var page = await OkAsync(t["emp"].Client.GetAsync($"{Search}?q=payslip"));
        var results = page.GetProperty("results").EnumerateArray().ToArray();
        Assert.Equal([letter, payroll], Ids(results));
        Assert.True(results[0].GetProperty("recommended").GetBoolean());
        Assert.False(results[1].GetProperty("recommended").GetBoolean());
        Assert.Equal(2, page.GetProperty("total").GetInt32());
        Assert.Equal(2, page.GetProperty("facets").GetProperty("content").GetInt32());

        // The editor may read the board item, so both best bets lead, in the order they were added.
        Assert.Equal([letter, board, payroll], Ids(await ResultsAsync(t["ed"].Client, "payslip")));

        // A best bet for other words does nothing here; removing it is audited and takes effect at once.
        Assert.False((await ResultsAsync(t["emp"].Client, "payslip dates"))[0].GetProperty("recommended").GetBoolean());
        var list = (await OkAsync(t["admin"].Client.GetAsync($"{Search}/best-bets"))).GetProperty("bestBets").EnumerateArray().ToArray();
        Assert.Equal(2, list.Length);
        await OkAsync(t["admin"].Client.DeleteAsync($"{Search}/best-bets/{bet.GetProperty("id").GetGuid()}"), HttpStatusCode.NoContent);
        Assert.Equal([payroll], Ids(await ResultsAsync(t["emp"].Client, "payslip")));
        Assert.Equal(3, await world.Database.AuditCountAsync(t.Id, AuditActions.SearchBestBetChanged));

        // Another tenant's search administrator cannot see or remove them.
        var other = await world.CreateTenantAsync("st-x-" + Guid.NewGuid().ToString("N")[..6], "workplace_plus");
        Assert.Empty((await OkAsync(other["admin"].Client.GetAsync($"{Search}/best-bets"))).GetProperty("bestBets").EnumerateArray());
        await OkAsync(other["admin"].Client.DeleteAsync($"{Search}/best-bets/{list[1].GetProperty("id").GetGuid()}"), HttpStatusCode.NotFound);
        Assert.Empty(await ResultsAsync(other["admin"].Client, "payslip"));
    }

    [Fact]
    public async Task Synonyms_let_one_word_find_the_others_in_the_language_they_are_set_for()
    {
        var t = await FreshAsync();
        var vacation = await NewsAsync(t["pia"].Client, "Plan your summer vacation", "Book your vacation days before June.");
        await DispatchAsync();
        Assert.Empty(await ResultsAsync(t["emp"].Client, "leave"));

        Assert.Equal(ProblemCodes.AuthzDenied, await ProblemKeyAsync(t["emp"].Client.PostAsJsonAsync($"{Search}/synonyms",
            new { terms = new[] { "leave", "vacation" } }), HttpStatusCode.Forbidden));
        Assert.Equal("validation.search.synonyms.word", await ProblemKeyAsync(t["admin"].Client.PostAsJsonAsync($"{Search}/synonyms",
            new { terms = new[] { "annual leave", "vacation" } }), HttpStatusCode.BadRequest));
        Assert.Equal("validation.search.synonyms.count", await ProblemKeyAsync(t["admin"].Client.PostAsJsonAsync($"{Search}/synonyms",
            new { terms = new[] { "Leave", "leave" } }), HttpStatusCode.BadRequest));

        // One set for every language: English and Arabic queries find the English item, with Arabic letter forms folded.
        var set = await OkAsync(t["admin"].Client.PostAsJsonAsync($"{Search}/synonyms", new { terms = new[] { "Leave", "vacation", "إجازة" } }), HttpStatusCode.Created);
        Assert.Equal(["leave", "vacation", "اجازه"], set.GetProperty("terms").EnumerateArray().Select(x => x.GetString()!).ToArray());
        Assert.Equal([vacation], Ids(await ResultsAsync(t["emp"].Client, "leave")));
        Assert.Equal([vacation], Ids(await ResultsAsync(t["emp"].Client, "اجازة")));
        await world.Database.AssertAuditedAsync(t.Id, AuditActions.SearchSynonymsChanged);

        // A set for Arabic applies to Arabic queries only.
        await OkAsync(t["admin"].Client.PostAsJsonAsync($"{Search}/synonyms", new { locale = "ar", terms = new[] { "عطلة", "trip", "vacation" } }), HttpStatusCode.Created);
        Assert.Equal([vacation], Ids(await ResultsAsync(t["emp"].Client, "عطلة")));
        Assert.Empty(await ResultsAsync(t["emp"].Client, "trip"));

        // Changing and removing a set apply at once and are audited.
        var id = set.GetProperty("id").GetGuid();
        await OkAsync(t["admin"].Client.PutAsJsonAsync($"{Search}/synonyms/{id}", new { terms = new[] { "holiday", "vacation" } }));
        Assert.Empty(await ResultsAsync(t["emp"].Client, "leave"));
        Assert.Equal([vacation], Ids(await ResultsAsync(t["emp"].Client, "holiday")));
        await OkAsync(t["admin"].Client.DeleteAsync($"{Search}/synonyms/{id}"), HttpStatusCode.NoContent);
        Assert.Empty(await ResultsAsync(t["emp"].Client, "holiday"));
        Assert.Equal(1, (await OkAsync(t["admin"].Client.GetAsync($"{Search}/synonyms"))).GetProperty("synonymSets").GetArrayLength());
        Assert.Equal(4, await world.Database.AuditCountAsync(t.Id, AuditActions.SearchSynonymsChanged));

        // Another tenant's sets are its own.
        var other = await world.CreateTenantAsync("st-y-" + Guid.NewGuid().ToString("N")[..6], "workplace_plus");
        Assert.Equal(0, (await OkAsync(other["admin"].Client.GetAsync($"{Search}/synonyms"))).GetProperty("synonymSets").GetArrayLength());
        await OkAsync(other["admin"].Client.PutAsJsonAsync($"{Search}/synonyms/{id}", new { terms = new[] { "a1", "b1" } }), HttpStatusCode.NotFound);
    }

    private Task<RetrievalResult> RetrieveAsync(TestTenant t, string user, string query, string useCase = AiUseCases.Assistant) =>
        world.Factory.AsUserAsync(t.Id, t[user].Id, sp => sp.GetRequiredService<ISearchRetrieval>().RetrieveAsync(new RetrievalRequest(query, useCase)));

    [Fact]
    public async Task Retrieval_returns_readable_passages_with_anchors_and_withholds_what_the_ai_policy_blocks()
    {
        var t = await FreshAsync();
        var open = await NewsAsync(t["pia"].Client, "Forklift inspection checklist", "Check the forklift brakes, horn and mast chains before every shift.");
        var closed = await NewsAsync(t["pia"].Client, "Forklift fleet budget", "Confidential forklift fleet budget for next year.", "confidential");
        await DispatchAsync();

        // The employee gets the open item's passages with what a citation needs; the confidential item is not readable,
        // so nothing is withheld, it simply is not there.
        var employee = await RetrieveAsync(t, "emp", "what should I check on a forklift before a shift?");
        Assert.NotEmpty(employee.Passages);
        Assert.All(employee.Passages, p => Assert.Equal(open, p.ItemId));
        var first = employee.Passages[0];
        Assert.Equal("content", first.Source);
        Assert.Equal($"/content/read/{open}", first.Anchor);
        Assert.Equal("en", first.Locale);
        Assert.Equal("internal", first.Classification);
        Assert.Equal(0, employee.Withheld);
        Assert.False(employee.Degraded);

        // The editor may read both, but the default AI policy sends no confidential content, so those passages are
        // withheld and counted (US3 scenario 2).
        var editor = await RetrieveAsync(t, "ed", "forklift fleet budget");
        Assert.DoesNotContain(editor.Passages, p => p.ItemId == closed);
        Assert.True(editor.Withheld >= 1);

        // A caller must name a known use case and a sensible number of passages.
        var invalid = await Assert.ThrowsAsync<AppException>(() => world.Factory.AsUserAsync(t.Id, t["emp"].Id,
            sp => sp.GetRequiredService<ISearchRetrieval>().RetrieveAsync(new RetrievalRequest("forklift", "chatter", 50))));
        Assert.Equal(ProblemCodes.ValidationFailed, invalid.Code);
        Assert.Equal(["topK", "useCase"], invalid.Errors!.Keys.Order().ToArray());

        // An item filter names both the source and the item (spec 025 FR-011).
        var half = await Assert.ThrowsAsync<AppException>(() => world.Factory.AsUserAsync(t.Id, t["emp"].Id,
            sp => sp.GetRequiredService<ISearchRetrieval>().RetrieveAsync(new RetrievalRequest("forklift", "assistant", ItemId: Guid.NewGuid()))));
        Assert.Equal(["itemId"], half.Errors!.Keys.ToArray());

        // Another tenant retrieves nothing of this one.
        var other = await world.CreateTenantAsync("st-z-" + Guid.NewGuid().ToString("N")[..6], "workplace_plus");
        var foreign = await RetrieveAsync(other, "admin", "forklift brakes horn mast chains");
        Assert.Empty(foreign.Passages);
        Assert.Equal(0, foreign.Withheld);
    }
}
