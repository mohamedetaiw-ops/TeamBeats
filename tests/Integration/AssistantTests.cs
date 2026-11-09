using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TeamBeats.Audit.Contracts;
using TeamBeats.Authorization.Contracts;
using TeamBeats.Messaging;
using TeamBeats.Modules.Agents;
using TeamBeats.Modules.Agents.Contracts;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Testing;
using Xunit;

namespace TeamBeats.Integration.Tests;

/// <summary>
/// Spec 014 slice 1 (T001 to T003, T006): cited answers only from passages the person may read, no evidence and policy
/// block without naming anything, answers in the person's language, history only for its user with delete and
/// retention, feedback that outlives the conversation, degraded states from the gateway, streaming, and no access
/// across users or tenants.
/// </summary>
[Collection(WorldCollection.Name)]
public sealed class AssistantTests(TenantWorld world)
{
    private const string Assistant = "/api/v1/assistant";
    private const string Items = "/api/v1/content/items";

    private async Task<TestTenant> FreshAsync()
    {
        var t = await world.CreateTenantAsync("as-" + Guid.NewGuid().ToString("N")[..8], "workplace_plus", "en", ["en", "ar"]);
        foreach (var name in new[] { "pia", "ed", "emp", "emp2", "aip" })
        {
            await world.SeedUserAsync(t, name, MembershipTypes.Employee);
        }
        await world.AssignAsync(t, "pia", BuiltInRoles.ContentPublisher, "tenant", null);
        await world.AssignAsync(t, "ed", BuiltInRoles.ContentEditor, "tenant", null);
        await world.AssignAsync(t, "aip", BuiltInRoles.AiPolicyAdministrator, "tenant", null);
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
        return body.GetProperty("code").GetString()!;
    }

    private async Task DispatchAsync()
    {
        var dispatcher = world.Factory.Services.GetRequiredService<OutboxDispatcher>();
        for (var i = 0; i < 20 && await dispatcher.DispatchBatchAsync(500, CancellationToken.None) > 0; i++)
        {
        }
    }

    private static object Body(string text) => new { blocks = new object[] { new { type = "paragraph", children = new object[] { new { text } } } } };

    private static async Task<Guid> NewsAsync(HttpClient client, string title, string body, string? classification = null, (string Title, string Body)? arabic = null)
    {
        var variants = new Dictionary<string, object> { ["en"] = new { title, body = Body(body) } };
        if (arabic is { } ar)
        {
            variants["ar"] = new { title = ar.Title, body = Body(ar.Body) };
        }
        var id = (await OkAsync(client.PostAsJsonAsync(Items, new { type = "news", classification, variants }), HttpStatusCode.Created)).GetProperty("id").GetGuid();
        await OkAsync(client.PostAsJsonAsync($"{Items}/{id}/publish", new { }));
        return id;
    }

    private static Task<JsonElement> AskAsync(HttpClient client, string question, Guid? conversationId = null) =>
        OkAsync(client.PostAsJsonAsync($"{Assistant}/turns", new { question, conversationId }));

    private Task<string?> TurnModelAsync(TestTenant t, Guid turnId) =>
        world.Database.TenantScalarAsync<string?>(t.Id, "select model from agents.turn where id = @id", ("id", turnId));

    [Fact]
    public async Task Answers_cite_only_passages_of_the_turn_that_the_person_may_read()
    {
        var t = await FreshAsync();
        var open = await NewsAsync(t["pia"].Client, "Forklift inspection checklist", "Check the forklift brakes, horn and mast chains before every shift.");
        await NewsAsync(t["pia"].Client, "Quarterly bonus pool", "Confidential quarterly bonus pool for the sales directors.", "confidential");
        await DispatchAsync();

        // The fake model echoes the question, so a marker for a passage that was never retrieved comes back and must go.
        var turn = await AskAsync(t["emp"].Client, "What do I check on a forklift before a shift? [9]");
        Assert.Equal(AssistantOutcomes.Answered, turn.GetProperty("outcome").GetString());
        var answer = turn.GetProperty("answer").GetString()!;
        Assert.Contains("[1]", answer);
        Assert.DoesNotContain("[9]", answer);
        Assert.True(turn.GetProperty("generated").GetBoolean());
        Assert.Equal("en", turn.GetProperty("language").GetString());
        var citation = Assert.Single(turn.GetProperty("citations").EnumerateArray());
        Assert.Equal(1, citation.GetProperty("n").GetInt32());
        Assert.Equal(open, citation.GetProperty("itemId").GetGuid());
        Assert.Equal("Forklift inspection checklist", citation.GetProperty("title").GetString());
        Assert.Equal($"/content/read/{open}", citation.GetProperty("anchor").GetString());
        Assert.False(turn.GetProperty("withheld").GetBoolean());
        var turnId = turn.GetProperty("id").GetGuid();
        Assert.NotNull(await TurnModelAsync(t, turnId));
        Assert.Equal(AssistantPromptVersion, await world.Database.TenantScalarAsync<string>(t.Id, "select prompt_version from agents.turn where id = @id", ("id", turnId)));

        // A follow-up stays in the conversation.
        var conversation = turn.GetProperty("conversationId").GetGuid();
        var next = await AskAsync(t["emp"].Client, "And the forklift horn?", conversation);
        Assert.Equal(conversation, next.GetProperty("conversationId").GetGuid());
        var view = await OkAsync(t["emp"].Client.GetAsync($"{Assistant}/conversations/{conversation}"));
        Assert.Equal(2, view.GetProperty("turns").GetArrayLength());
        Assert.Equal("What do I check on a forklift before a shift? [9]", view.GetProperty("title").GetString());

        // The employee cannot read the confidential item, so it is never retrieved, cited or named: nothing is found and
        // no model is called.
        var hidden = await AskAsync(t["emp"].Client, "How big is the quarterly bonus pool for sales directors?");
        Assert.Equal(AssistantOutcomes.NoEvidence, hidden.GetProperty("outcome").GetString());
        Assert.Equal(JsonValueKind.Null, hidden.GetProperty("answer").ValueKind);
        Assert.Empty(hidden.GetProperty("citations").EnumerateArray());
        Assert.DoesNotContain("bonus pool", hidden.GetRawText().Replace(hidden.GetProperty("question").GetString()!, string.Empty, StringComparison.Ordinal),
            StringComparison.OrdinalIgnoreCase);
        Assert.Null(await TurnModelAsync(t, hidden.GetProperty("id").GetGuid()));

        // The editor may read it, but the default AI policy sends no confidential content: the assistant says content was
        // kept back by policy, without naming it, and calls no model.
        var blocked = await AskAsync(t["ed"].Client, "How big is the quarterly bonus pool for sales directors?");
        Assert.Equal(AssistantOutcomes.PolicyBlocked, blocked.GetProperty("outcome").GetString());
        Assert.Empty(blocked.GetProperty("citations").EnumerateArray());
        Assert.True(blocked.GetProperty("withheld").GetBoolean());
        Assert.Null(await TurnModelAsync(t, blocked.GetProperty("id").GetGuid()));

        // A question longer than 2,000 characters is refused before anything runs.
        Assert.Equal("validation.assistant.question", await ProblemKeyAsync(t["emp"].Client.PostAsJsonAsync($"{Assistant}/turns",
            new { question = new string('a', 2001) }), HttpStatusCode.BadRequest));

        // Another tenant retrieves nothing of this one.
        var other = await world.CreateTenantAsync("as-x-" + Guid.NewGuid().ToString("N")[..6], "workplace_plus");
        var foreign = await AskAsync(other["admin"].Client, "What do I check on a forklift before a shift?");
        Assert.Equal(AssistantOutcomes.NoEvidence, foreign.GetProperty("outcome").GetString());
    }

    private const string AssistantPromptVersion = "knowledge.v1";

    [Fact]
    public async Task An_arabic_question_gets_an_arabic_answer_and_streams()
    {
        var t = await FreshAsync();
        var parking = await NewsAsync(t["pia"].Client, "Parking permits", "Ask the security office for a parking permit.",
            arabic: ("تصاريح المواقف", "يمكنك طلب تصريح موقف السيارات من مكتب الأمن."));
        await DispatchAsync();

        var turn = await AskAsync(t["emp"].Client, "كيف أطلب تصريح موقف السيارات؟");
        Assert.Equal(AssistantOutcomes.Answered, turn.GetProperty("outcome").GetString());
        Assert.Equal("ar", turn.GetProperty("language").GetString());
        Assert.Contains("هذا رد تجريبي", turn.GetProperty("answer").GetString());
        Assert.Equal(parking, turn.GetProperty("citations")[0].GetProperty("itemId").GetGuid());

        // Streamed: text arrives as delta events, then the stored turn with its citations.
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{Assistant}/turns") { Content = JsonContent.Create(new { question = "Where do I get a parking permit?" }) };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        var response = await t["emp"].Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/event-stream", response.Content.Headers.ContentType?.MediaType);
        var text = await response.Content.ReadAsStringAsync();
        Assert.Contains("event: delta", text);
        var done = text.Split("\n\n").Single(e => e.StartsWith("event: done", StringComparison.Ordinal));
        var stored = JsonDocument.Parse(done[(done.IndexOf("data: ", StringComparison.Ordinal) + 6)..]).RootElement;
        Assert.Equal(AssistantOutcomes.Answered, stored.GetProperty("outcome").GetString());
        Assert.Equal(parking, stored.GetProperty("citations")[0].GetProperty("itemId").GetGuid());
        var list = (await OkAsync(t["emp"].Client.GetAsync($"{Assistant}/conversations"))).GetProperty("conversations");
        Assert.Equal(2, list.GetArrayLength());

        // A refused stream still answers with a validation problem before anything is streamed.
        using var bad = new HttpRequestMessage(HttpMethod.Post, $"{Assistant}/turns") { Content = JsonContent.Create(new { question = " " }) };
        bad.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        Assert.Equal(HttpStatusCode.BadRequest, (await t["emp"].Client.SendAsync(bad)).StatusCode);
    }

    [Fact]
    public async Task Provider_failures_show_as_degraded_turns_and_are_kept_in_history()
    {
        var t = await FreshAsync();
        await NewsAsync(t["pia"].Client, "Canteen opening hours", "The canteen opens at seven and closes at three.");
        await DispatchAsync();

        var failed = await AskAsync(t["emp"].Client, "When does the canteen open? [fake:500]");
        Assert.Equal(AssistantOutcomes.Unavailable, failed.GetProperty("outcome").GetString());
        Assert.Equal("provider_unavailable", failed.GetProperty("reason").GetString());
        Assert.Equal(JsonValueKind.Null, failed.GetProperty("answer").ValueKind);

        var refused = await AskAsync(t["emp"].Client, "When does the canteen open? [fake:refuse]");
        Assert.Equal(AssistantOutcomes.Unavailable, refused.GetProperty("outcome").GetString());
        Assert.Equal("refused", refused.GetProperty("reason").GetString());

        // An answer that cites nothing is shown as it is, with no citations invented for it.
        var uncited = await AskAsync(t["emp"].Client, "When does the canteen open? [fake:nocite]");
        Assert.Equal(AssistantOutcomes.Answered, uncited.GetProperty("outcome").GetString());
        Assert.Empty(uncited.GetProperty("citations").EnumerateArray());

        var conversation = await OkAsync(t["emp"].Client.GetAsync($"{Assistant}/conversations/{failed.GetProperty("conversationId").GetGuid()}"));
        Assert.Equal("unavailable", conversation.GetProperty("turns")[0].GetProperty("outcome").GetString());
    }

    [Fact]
    public async Task History_and_feedback_belong_to_their_user_and_end_with_retention()
    {
        var t = await FreshAsync();
        await NewsAsync(t["pia"].Client, "Visitor badges", "Visitors collect a badge at reception with their ID.");
        await DispatchAsync();
        var first = await AskAsync(t["emp"].Client, "How do visitors get a badge?");
        await AskAsync(t["emp"].Client, "Where is reception for visitor badges?");
        var conversation = first.GetProperty("conversationId").GetGuid();
        var turn = first.GetProperty("id").GetGuid();

        // Another user of the same tenant, and another tenant, see none of it and cannot rate or delete it.
        Assert.Equal(0, (await OkAsync(t["emp2"].Client.GetAsync($"{Assistant}/conversations"))).GetProperty("conversations").GetArrayLength());
        await OkAsync(t["emp2"].Client.GetAsync($"{Assistant}/conversations/{conversation}"), HttpStatusCode.NotFound);
        await OkAsync(t["emp2"].Client.PostAsJsonAsync($"{Assistant}/turns", new { question = "Visitor badges?", conversationId = conversation }), HttpStatusCode.NotFound);
        await OkAsync(t["emp2"].Client.PostAsJsonAsync($"{Assistant}/turns/{turn}/feedback", new { helpful = true }), HttpStatusCode.NotFound);
        await OkAsync(t["emp2"].Client.DeleteAsync($"{Assistant}/conversations/{conversation}"), HttpStatusCode.NotFound);
        var other = await world.CreateTenantAsync("as-y-" + Guid.NewGuid().ToString("N")[..6], "workplace_plus");
        await OkAsync(other["admin"].Client.GetAsync($"{Assistant}/conversations/{conversation}"), HttpStatusCode.NotFound);
        await OkAsync(other["admin"].Client.PostAsJsonAsync($"{Assistant}/turns/{turn}/feedback", new { helpful = true }), HttpStatusCode.NotFound);

        // Feedback is validated, can be changed, and shows on the turn.
        Assert.Equal("validation.assistant.feedback", await ProblemKeyAsync(t["emp"].Client.PostAsJsonAsync($"{Assistant}/turns/{turn}/feedback", new { }),
            HttpStatusCode.BadRequest));
        Assert.Equal("validation.assistant.feedback_reason", await ProblemKeyAsync(t["emp"].Client.PostAsJsonAsync($"{Assistant}/turns/{turn}/feedback",
            new { helpful = false, reason = "rude" }), HttpStatusCode.BadRequest));
        await OkAsync(t["emp"].Client.PostAsJsonAsync($"{Assistant}/turns/{turn}/feedback", new { helpful = true }), HttpStatusCode.NoContent);
        await OkAsync(t["emp"].Client.PostAsJsonAsync($"{Assistant}/turns/{turn}/feedback", new { helpful = false, reason = "incomplete" }), HttpStatusCode.NoContent);
        var view = await OkAsync(t["emp"].Client.GetAsync($"{Assistant}/conversations/{conversation}"));
        Assert.False(view.GetProperty("turns")[0].GetProperty("helpful").GetBoolean());
        Assert.Equal("incomplete", await world.Database.TenantScalarAsync<string>(t.Id, "select reason from agents.feedback where turn_id = @id", ("id", turn)));

        // Deleting one conversation, then all, is audited; the feedback stays for evaluation without the text.
        await OkAsync(t["emp"].Client.DeleteAsync($"{Assistant}/conversations/{conversation}"), HttpStatusCode.NoContent);
        await world.Database.AssertAuditedAsync(t.Id, AuditActions.AssistantHistoryDeleted, conversation.ToString());
        Assert.Equal(1L, await world.Database.TenantScalarAsync<long>(t.Id, "select count(*) from agents.feedback where turn_id = @id", ("id", turn)));
        Assert.Equal(0L, await world.Database.TenantScalarAsync<long>(t.Id, "select count(*) from agents.turn where conversation_id = @id", ("id", conversation)));
        Assert.Equal(1, (await OkAsync(t["emp"].Client.DeleteAsync($"{Assistant}/conversations"))).GetProperty("removed").GetInt32());
        Assert.Equal(0, (await OkAsync(t["emp"].Client.GetAsync($"{Assistant}/conversations"))).GetProperty("conversations").GetArrayLength());
        Assert.Equal(2, await world.Database.AuditCountAsync(t.Id, AuditActions.AssistantHistoryDeleted));

        // Only the AI policy administrator sets retention, from 1 to 365 days; the change is audited.
        Assert.Equal(30, (await OkAsync(t["aip"].Client.GetAsync($"{Assistant}/settings"))).GetProperty("retentionDays").GetInt32());
        await OkAsync(t["emp"].Client.GetAsync($"{Assistant}/settings"), HttpStatusCode.Forbidden);
        await OkAsync(t["emp"].Client.PutAsJsonAsync($"{Assistant}/settings", new { retentionDays = 7 }), HttpStatusCode.Forbidden);
        Assert.Equal("validation.assistant.retention", await ProblemKeyAsync(t["aip"].Client.PutAsJsonAsync($"{Assistant}/settings", new { retentionDays = 400 }),
            HttpStatusCode.BadRequest));
        Assert.Equal(7, (await OkAsync(t["aip"].Client.PutAsJsonAsync($"{Assistant}/settings", new { retentionDays = 7 }))).GetProperty("retentionDays").GetInt32());
        await world.Database.AssertAuditedAsync(t.Id, AuditActions.AssistantRetentionChanged);

        // The retention job removes conversations idle past the period, and nothing that is newer.
        var kept = (await AskAsync(t["emp"].Client, "How do visitors get a badge?")).GetProperty("conversationId").GetGuid();
        Assert.Equal((0, 0), await PurgeAsync(t, DateTimeOffset.UtcNow.AddDays(6)));
        Assert.Equal((1, 0), await PurgeAsync(t, DateTimeOffset.UtcNow.AddDays(8)));
        await OkAsync(t["emp"].Client.GetAsync($"{Assistant}/conversations/{kept}"), HttpStatusCode.NotFound);
        Assert.Equal((0, 1), await PurgeAsync(t, DateTimeOffset.UtcNow.AddMonths(26)));
    }

    private Task<(int, int)> PurgeAsync(TestTenant t, DateTimeOffset now) =>
        world.Factory.InScopeAsync(t.Id, async sp =>
        {
            var done = await sp.GetRequiredService<AssistantHistory>().PurgeAsync(now, CancellationToken.None);
            await sp.GetRequiredService<IDbSession>().CommitAsync();
            return done;
        });
}
