using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using TeamBeats.Audit.Contracts;
using TeamBeats.Authorization.Contracts;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Testing;
using Xunit;

namespace TeamBeats.Integration.Tests;

/// <summary>
/// Spec 010 R3 T018 (US9, FR-033 to FR-035): skills suggested from what a person published. Nothing is read until the
/// person turns suggestions on; a run reads only their own published, named, non-Confidential items, suggests only
/// skills from the tenant list they have not declared or dismissed, and cites the items. Accepting declares the skill,
/// dismissing keeps it away, turning off deletes open suggestions, three runs a day, Workplace has none, another tenant
/// finds nothing. The model is the fake of the test factory. Names and content are fictional.
/// </summary>
[Collection(WorldCollection.Name)]
public sealed class SkillSuggestionTests(TenantWorld world)
{
    private const string Suggestions = "/api/v1/me/skills/suggestions";

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
        var problem = JsonDocument.Parse(text).RootElement;
        return problem.TryGetProperty("detailKey", out var key) ? key.GetString()!
            : problem.GetProperty("errors").EnumerateObject().First().Value[0].GetProperty("key").GetString()!;
    }

    private static Dictionary<string, string> Names(string en, string ar) => new() { ["en"] = en, ["ar"] = ar };

    private static object Body(string text) => new { blocks = new object[] { new { type = "paragraph", children = new object[] { new { text } } } } };

    private sealed record World(TestTenant T, Guid Contract, Guid Excel, Guid Payroll, Guid Article, Guid Post);

    private async Task<World> SetupAsync()
    {
        var t = await world.CreateTenantAsync("sgs-" + Guid.NewGuid().ToString("N")[..8], "workplace_plus");
        foreach (var name in new[] { "omar", "laila" })
        {
            await world.SeedUserAsync(t, name, MembershipTypes.Employee);
            await world.AssignAsync(t, name, BuiltInRoles.ContentPublisher, "tenant", null);
        }
        var admin = t["admin"].Client;
        var current = await admin.GetAsync("/api/v1/people/skills");
        var put = new HttpRequestMessage(HttpMethod.Put, "/api/v1/people/skills")
        {
            Content = JsonContent.Create(new
            {
                skills = new[] { new { names = Names("Contract law", "قانون العقود") }, new { names = Names("Excel", "إكسل") }, new { names = Names("Payroll", "الرواتب") } },
            }),
        };
        put.Headers.TryAddWithoutValidation("If-Match", current.Headers.ETag!.Tag);
        var list = await OkAsync(admin.SendAsync(put));
        Guid Id(string en) => list.GetProperty("skills").EnumerateArray().Single(s => s.GetProperty("names").GetProperty("en").GetString() == en).GetProperty("id").GetGuid();

        async Task<Guid> PublishAsync(string user, string title, string text, string classification = "internal")
        {
            var item = await OkAsync(t[user].Client.PostAsJsonAsync("/api/v1/content/items", new
            {
                type = "news", classification,
                variants = new Dictionary<string, object> { ["en"] = new { title, body = Body(text) } },
            }), HttpStatusCode.Created);
            var id = item.GetProperty("id").GetGuid();
            await OkAsync(t[user].Client.PostAsJsonAsync($"/api/v1/content/items/{id}/publish", new { material = true }));
            return id;
        }

        var article = await PublishAsync("omar", "Notes for new starters", "How we review contract law questions before signing.");
        await PublishAsync("omar", "Pay rounds", "The payroll calendar for the year.", "confidential");
        await PublishAsync("laila", "Month end", "Payroll closes on the 25th.");
        var community = (await OkAsync(admin.PostAsJsonAsync("/api/v1/engagement/communities", new { names = Names("Finance corner", "ركن المالية"), kind = "open" }), HttpStatusCode.Created))
            .GetProperty("id").GetGuid();
        await OkAsync(admin.PutAsJsonAsync($"/api/v1/engagement/communities/{community}/members", new { userId = t["omar"].Id }));
        var post = (await OkAsync(t["omar"].Client.PostAsJsonAsync($"/api/v1/engagement/communities/{community}/posts", new { body = Body("My Excel template for supplier contract law checks") }), HttpStatusCode.Created))
            .GetProperty("post").GetProperty("id").GetGuid();
        return new World(t, Id("Contract law"), Id("Excel"), Id("Payroll"), article, post);
    }

    private static Guid[] SkillIds(JsonElement view) => view.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("skillId").GetGuid()).ToArray();

    [Fact]
    public async Task Suggestions_read_only_the_persons_own_published_items_after_consent_and_cite_them()
    {
        var w = await SetupAsync();
        var omar = w.T["omar"].Client;

        // Off until Omar turns it on: nothing runs and nothing is read.
        var before = await OkAsync(omar.GetAsync(Suggestions));
        Assert.True(before.GetProperty("available").GetBoolean());
        Assert.False(before.GetProperty("on").GetBoolean());
        var prompts = world.Factory.OpenRouter.UserPrompts.Count;
        Assert.Equal("conflict.people.suggestions_off", await ProblemKeyAsync(omar.PostAsync($"{Suggestions}:run", null), HttpStatusCode.Conflict));
        Assert.Equal(prompts, world.Factory.OpenRouter.UserPrompts.Count);
        Assert.Equal("validation.people.suggestions_on", await ProblemKeyAsync(omar.PutAsJsonAsync(Suggestions, new { }), HttpStatusCode.BadRequest));

        Assert.True((await OkAsync(omar.PutAsJsonAsync(Suggestions, new { on = true }))).GetProperty("on").GetBoolean());
        await world.Database.AssertAuditedAsync(w.T.Id, AuditActions.PeopleSkillSuggestionsChanged);
        var run = await OkAsync(omar.PostAsync($"{Suggestions}:run", null));
        Assert.Equal("suggested", run.GetProperty("outcome").GetString());
        Assert.True(run.GetProperty("aiGenerated").GetBoolean());
        Assert.Equal(2, run.GetProperty("read").GetInt32());
        var view = run.GetProperty("suggestions");
        Assert.Equal(new[] { w.Contract, w.Excel }.Order(), SkillIds(view).Order());
        var contract = view.GetProperty("items").EnumerateArray().Single(i => i.GetProperty("skillId").GetGuid() == w.Contract);
        Assert.Equal(new[] { w.Article, w.Post }.Order(), contract.GetProperty("from").EnumerateArray().Select(f => f.GetProperty("id").GetGuid()).Order());
        Assert.Equal(2, view.GetProperty("runsLeft").GetInt32());
        // Only Omar's named, non-Confidential items went to the model: not his Confidential item, not Laila's article.
        var prompt = world.Factory.OpenRouter.UserPrompts.Last(p => p.Contains("Notes for new starters", StringComparison.Ordinal));
        Assert.DoesNotContain("Pay rounds", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("Month end", prompt, StringComparison.Ordinal);
        await world.Database.AssertAuditedAsync(w.T.Id, AuditActions.PeopleSkillsSuggested);

        // Laila never sees Omar's suggestions.
        Assert.Empty((await OkAsync(w.T["laila"].Client.GetAsync(Suggestions))).GetProperty("items").EnumerateArray());
        Assert.Equal(HttpStatusCode.NotFound, (await w.T["laila"].Client.PostAsJsonAsync($"{Suggestions}/{w.Contract}:accept", new { proficiency = "good" })).StatusCode);

        // Accepting declares the skill; dismissing keeps Excel away from later runs.
        Assert.Equal("validation.people.proficiency", await ProblemKeyAsync(omar.PostAsJsonAsync($"{Suggestions}/{w.Contract}:accept", new { proficiency = "guru" }), HttpStatusCode.BadRequest));
        var accepted = await OkAsync(omar.PostAsJsonAsync($"{Suggestions}/{w.Contract}:accept", new { proficiency = "good" }));
        Assert.Equal(new[] { w.Excel }, SkillIds(accepted));
        var mine = await OkAsync(omar.GetAsync("/api/v1/me/skills"));
        Assert.Equal("good", mine.GetProperty("items").EnumerateArray().Single(s => s.GetProperty("skillId").GetGuid() == w.Contract).GetProperty("proficiency").GetString());
        Assert.Empty(SkillIds(await OkAsync(omar.PostAsync($"{Suggestions}/{w.Excel}:dismiss", null))));
        Assert.Equal(HttpStatusCode.NotFound, (await omar.PostAsync($"{Suggestions}/{w.Excel}:dismiss", null)).StatusCode);

        var again = await OkAsync(omar.PostAsync($"{Suggestions}:run", null));
        Assert.Equal("nothing_found", again.GetProperty("outcome").GetString());
        Assert.Empty(SkillIds(again.GetProperty("suggestions")));
        await OkAsync(omar.PostAsync($"{Suggestions}:run", null));
        Assert.Equal("conflict.people.suggestion_limit", await ProblemKeyAsync(omar.PostAsync($"{Suggestions}:run", null), HttpStatusCode.Conflict));

        // Another tenant reaches none of it.
        var other = await SetupAsync();
        await OkAsync(other.T["omar"].Client.PutAsJsonAsync(Suggestions, new { on = true }));
        Assert.Equal(HttpStatusCode.NotFound, (await other.T["omar"].Client.PostAsJsonAsync($"{Suggestions}/{w.Excel}:accept", new { proficiency = "good" })).StatusCode);
    }

    [Fact]
    public async Task Turning_suggestions_off_deletes_open_ones_and_Workplace_has_none()
    {
        var w = await SetupAsync();
        var laila = w.T["laila"].Client;
        await OkAsync(laila.PutAsJsonAsync(Suggestions, new { on = true }));
        var run = await OkAsync(laila.PostAsync($"{Suggestions}:run", null));
        Assert.Equal(new[] { w.Payroll }, SkillIds(run.GetProperty("suggestions")));

        var off = await OkAsync(laila.PutAsJsonAsync(Suggestions, new { on = false }));
        Assert.False(off.GetProperty("on").GetBoolean());
        Assert.Empty(SkillIds(off));
        Assert.Equal(0, await world.Database.CountInTenantAsync(w.T.Id, "select count(*) from people.skill_suggestion where user_id = @u", ("u", w.T["laila"].Id)));
        Assert.Equal(HttpStatusCode.NotFound, (await laila.PostAsJsonAsync($"{Suggestions}/{w.Payroll}:accept", new { proficiency = "basic" })).StatusCode);

        var t = await world.CreateTenantAsync("sgw-" + Guid.NewGuid().ToString("N")[..8], "workplace");
        await world.SeedUserAsync(t, "omar", MembershipTypes.Employee);
        Assert.False((await OkAsync(t["omar"].Client.GetAsync(Suggestions))).GetProperty("available").GetBoolean());
        Assert.Equal(HttpStatusCode.Forbidden, (await t["omar"].Client.PutAsJsonAsync(Suggestions, new { on = true })).StatusCode);
    }
}
