using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TeamBeats.Authorization.Contracts;
using TeamBeats.Messaging;
using TeamBeats.Modules.Agents.Contracts;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Testing;
using Xunit;

namespace TeamBeats.Integration.Tests;

/// <summary>
/// Spec 025 T009 and T010 (US4, FR-010, FR-011, FR-018): clauses of the version in force are search units with stable
/// anchors, a superseded version is never retrieved, the assistant cites policy title, version and clause, a group and a
/// subsidiary policy are both named, a question no policy answers gets no answer, "Ask about this policy" keeps a
/// conversation to that policy, and a policy the person may not read is never used, named or reachable, in this tenant
/// or another.
/// </summary>
[Collection(WorldCollection.Name)]
public sealed class PolicyQuestionTests(TenantWorld world)
{
    private const string Policies = "/api/v1/policies";
    private const string Turns = "/api/v1/assistant/turns";

    private sealed record Org(TestTenant T, Guid Holding, Guid Gulf);

    private async Task<Org> FreshAsync()
    {
        var t = await world.CreateTenantAsync("pq-" + Guid.NewGuid().ToString("N")[..8], "workplace_plus", "en", ["en", "ar"]);
        var holding = await world.CreateUnitAsync(t, "Holding", null, "organization");
        var gulf = await world.CreateUnitAsync(t, "Gulf", holding, "subsidiary");
        await world.CreateUnitAsync(t, "Egypt", holding, "subsidiary");
        foreach (var (name, unit) in new[] { ("co", "Gulf"), ("legal", "Gulf"), ("ali", "Gulf"), ("cai", "Egypt") })
        {
            await world.SeedUserAsync(t, name, MembershipTypes.Employee);
            await world.AddMembershipAsync(t, name, unit);
        }
        await world.AssignAsync(t, "co", BuiltInRoles.ComplianceOfficer, "tenant", null);
        return new Org(t, holding, gulf);
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
        return body.TryGetProperty("errors", out var errors) ? errors.EnumerateObject().First().Value[0].GetProperty("key").GetString()! : body.GetProperty("code").GetString()!;
    }

    private async Task DispatchAsync()
    {
        var dispatcher = world.Factory.Services.GetRequiredService<OutboxDispatcher>();
        for (var i = 0; i < 20 && await dispatcher.DispatchBatchAsync(500, CancellationToken.None) > 0; i++)
        {
        }
    }

    private static object Body(string text) => new { blocks = new object[] { new { type = "paragraph", children = new[] { new { text } } } } };

    private static object Clauses(params (string Title, string Text)[] clauses) => clauses.Select(c => new
    {
        level = 1,
        titles = new Dictionary<string, string> { ["en"] = c.Title, ["ar"] = c.Title + " ع" },
        bodies = new Dictionary<string, object> { ["en"] = Body(c.Text), ["ar"] = Body(c.Text + " نص") },
    }).ToArray();

    /// <summary>A policy of the unit, approved by legal and in force as version 1.</summary>
    private async Task<Guid> PublishedAsync(Org o, string code, string title, Guid unit, params (string Title, string Text)[] clauses)
    {
        var t = o.T;
        var titles = new Dictionary<string, string> { ["en"] = title, ["ar"] = title + " ع" };
        var id = (await OkAsync(t["co"].Client.PostAsJsonAsync(Policies, new
        {
            code, names = titles, orgUnitId = unit, route = new[] { new { approvers = new[] { t["legal"].Id } } },
        }), HttpStatusCode.Created)).GetProperty("id").GetGuid();
        await ReviseAsync(o, id, title, clauses);
        return id;
    }

    private async Task ReviseAsync(Org o, Guid id, string title, params (string Title, string Text)[] clauses)
    {
        var t = o.T;
        var titles = new Dictionary<string, string> { ["en"] = title, ["ar"] = title + " ع" };
        await OkAsync(t["co"].Client.PutAsJsonAsync($"{Policies}/{id}/draft", new { titles, clauses = Clauses(clauses), material = true }));
        await OkAsync(t["co"].Client.PostAsJsonAsync($"{Policies}/{id}/submit", new { }));
        var task = (await OkAsync(t["legal"].Client.GetAsync("/api/v1/tasks"))).EnumerateArray().Single(i => i.GetProperty("source").GetString() == "policy_approval");
        await OkAsync(t["legal"].Client.PostAsJsonAsync($"/api/v1/tasks/{task.GetProperty("taskId").GetGuid()}/decision", new { outcome = "approve" }));
        await DispatchAsync();
    }

    private static Task<JsonElement> AskAsync(HttpClient client, string question, Guid? conversationId = null, Guid? policy = null) =>
        OkAsync(client.PostAsJsonAsync(Turns, new
        {
            question, conversationId, scope = policy is { } p ? new { source = "policies", itemId = p } : null,
        }));

    private static Guid[] Cited(JsonElement turn) => turn.GetProperty("citations").EnumerateArray().Select(c => c.GetProperty("itemId").GetGuid()).ToArray();

    [Fact]
    public async Task The_assistant_cites_the_policy_clause_in_force_names_each_policy_that_applies_and_answers_nothing_else()
    {
        var o = await FreshAsync();
        var t = o.T;
        var group = await PublishedAsync(o, "GRP-GIFTS", "Gifts and hospitality", o.Holding,
            ("Travel approvals", "Book flights and hotels through the travel desk."),
            ("Gifts from suppliers", "Employees may accept a gift from a supplier worth up to 50 dollars."));
        var gulf = await PublishedAsync(o, "GULF-GIFTS", "Gulf gifts rules", o.Gulf,
            ("Supplier gifts in the Gulf", "In the Gulf every gift from a supplier must be declared to the compliance office."));

        // T009: each clause of the version in force is a unit with its anchor and the version number.
        var units = await world.Database.TenantScalarAsync<long>(t.Id,
            "select count(*) from search.unit where source = 'policies' and item_id = @id and locale = 'en' and link like '%#clause-%' and version = 1", ("id", group));
        Assert.Equal(2, units);

        // Scenario 1: the answer cites the policy title, version and clause, and links to the clause anchor.
        var reading = await OkAsync(t["ali"].Client.GetAsync($"{Policies}/{group}/current"));
        var clauseKeys = reading.GetProperty("version").GetProperty("clauses").EnumerateArray().Select(c => c.GetProperty("key").GetString()!).ToList();
        var answer = await AskAsync(t["cai"].Client, "Can I accept a gift from a supplier?");
        Assert.Equal(AssistantOutcomes.Answered, answer.GetProperty("outcome").GetString());
        var citation = answer.GetProperty("citations")[0];
        Assert.Equal(group, citation.GetProperty("itemId").GetGuid());
        Assert.Equal("policy", citation.GetProperty("kind").GetString());
        Assert.Equal("Gifts and hospitality", citation.GetProperty("title").GetString());
        Assert.Equal(1, citation.GetProperty("version").GetInt32());
        Assert.Equal($"/policies/{group}#clause-{clauseKeys[1]}", citation.GetProperty("anchor").GetString());

        // Scenario 6: the Egypt employee may not read the Gulf policy, so it is never retrieved, cited or named.
        Assert.DoesNotContain(gulf, Cited(answer));
        Assert.DoesNotContain("Gulf gifts rules", answer.GetRawText(), StringComparison.Ordinal);
        var hidden = await AskAsync(t["cai"].Client, "Must a gift be declared to the compliance office in the Gulf?");
        Assert.DoesNotContain(gulf, Cited(hidden));
        Assert.DoesNotContain("Gulf gifts rules", hidden.GetRawText(), StringComparison.Ordinal);

        // Scenario 2: in the Gulf both policies apply, and both come to the model to be named (the fake echoes the markers).
        var both = await AskAsync(t["ali"].Client, "Can I accept a gift from a supplier? [1][2][3][4]");
        Assert.Equal(new[] { group, gulf }.Order(), Cited(both).Distinct().Order());

        // Scenario 3: a question no policy answers gets no answer and no model call.
        var none = await AskAsync(t["ali"].Client, "What is the dress code for the summer party?");
        Assert.Equal(AssistantOutcomes.NoEvidence, none.GetProperty("outcome").GetString());
        Assert.Equal(JsonValueKind.Null, none.GetProperty("answer").ValueKind);

        // Scenario 5 and T009: a new version in force replaces the old clauses; the superseded text is never retrieved.
        await ReviseAsync(o, gulf, "Gulf gifts rules", ("Supplier gifts in the Gulf", "In the Gulf no gift from a supplier may be accepted."));
        Assert.Equal(0, await world.Database.TenantScalarAsync<long>(t.Id,
            "select count(*) from search.unit where source = 'policies' and item_id = @id and (version <> 2 or body like '%declared%')", ("id", gulf)));
        var revised = await AskAsync(t["ali"].Client, "Can I accept a gift from a supplier in the Gulf? [1][2][3][4]");
        var gulfCitations = revised.GetProperty("citations").EnumerateArray().Where(c => c.GetProperty("itemId").GetGuid() == gulf).ToList();
        Assert.NotEmpty(gulfCitations);
        Assert.All(gulfCitations, c => Assert.Equal(2, c.GetProperty("version").GetInt32()));
    }

    [Fact]
    public async Task Ask_about_this_policy_keeps_the_conversation_to_that_policy_for_readers_only()
    {
        var o = await FreshAsync();
        var t = o.T;
        var group = await PublishedAsync(o, "GRP-GIFTS", "Gifts and hospitality", o.Holding,
            ("Gifts from suppliers", "Employees may accept a gift from a supplier worth up to 50 dollars."));
        var gulf = await PublishedAsync(o, "GULF-GIFTS", "Gulf gifts rules", o.Gulf,
            ("Supplier gifts in the Gulf", "In the Gulf every gift from a supplier must be declared to the compliance office."));

        // The policy page offers the entry to its readers.
        Assert.True((await OkAsync(t["ali"].Client.GetAsync($"{Policies}/{gulf}"))).GetProperty("rights").GetProperty("ask").GetBoolean());

        // Scenario 4: answers come from that policy only, and later turns keep the scope.
        var first = await AskAsync(t["ali"].Client, "Can I accept a gift from a supplier? [1][2]", policy: gulf);
        Assert.Equal(AssistantOutcomes.Answered, first.GetProperty("outcome").GetString());
        Assert.All(Cited(first), id => Assert.Equal(gulf, id));
        Assert.NotEmpty(Cited(first));
        var conversation = first.GetProperty("conversationId").GetGuid();
        var next = await AskAsync(t["ali"].Client, "And what about a gift worth 50 dollars? [1][2]", conversation);
        Assert.All(Cited(next), id => Assert.Equal(gulf, id));
        var view = await OkAsync(t["ali"].Client.GetAsync($"/api/v1/assistant/conversations/{conversation}"));
        Assert.Equal("policies", view.GetProperty("scope").GetProperty("source").GetString());
        Assert.Equal(gulf, view.GetProperty("scope").GetProperty("itemId").GetGuid());

        // A question the policy does not answer gets no answer, even when another policy would.
        var travel = await AskAsync(t["ali"].Client, "How do I book flights and hotels?", policy: group);
        Assert.Equal(AssistantOutcomes.NoEvidence, travel.GetProperty("outcome").GetString());
        Assert.Empty(Cited(travel));

        // An unscoped conversation has no scope.
        var plain = await AskAsync(t["ali"].Client, "Can I accept a gift from a supplier?");
        Assert.Equal(JsonValueKind.Null,
            (await OkAsync(t["ali"].Client.GetAsync($"/api/v1/assistant/conversations/{plain.GetProperty("conversationId").GetGuid()}"))).GetProperty("scope").ValueKind);

        // Scenario 6: a policy the person may not read cannot be a scope, and looks like one that does not exist.
        Assert.Equal(HttpStatusCode.NotFound, (await t["cai"].Client.PostAsJsonAsync(Turns,
            new { question = "Must a gift be declared?", scope = new { source = "policies", itemId = gulf } })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await t["cai"].Client.PostAsJsonAsync(Turns,
            new { question = "Must a gift be declared?", scope = new { source = "policies", itemId = Guid.NewGuid() } })).StatusCode);
        Assert.Equal("validation.assistant.scope", await ProblemKeyAsync(t["ali"].Client.PostAsJsonAsync(Turns,
            new { question = "What is new?", scope = new { source = "content", itemId = group } }), HttpStatusCode.BadRequest));

        // Another tenant can neither scope to this tenant's policy nor reach the conversation.
        var other = await world.CreateTenantAsync("pq-x-" + Guid.NewGuid().ToString("N")[..6], "workplace_plus");
        Assert.Equal(HttpStatusCode.NotFound, (await other["admin"].Client.PostAsJsonAsync(Turns,
            new { question = "Can I accept a gift?", scope = new { source = "policies", itemId = gulf } })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other["admin"].Client.GetAsync($"/api/v1/assistant/conversations/{conversation}")).StatusCode);
    }
}
