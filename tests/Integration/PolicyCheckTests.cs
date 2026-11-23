using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using TeamBeats.Audit.Contracts;
using TeamBeats.Authorization.Contracts;
using TeamBeats.Modules.Policies;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Testing;
using Xunit;

namespace TeamBeats.Integration.Tests;

/// <summary>
/// Spec 025 T014 (US6, FR-013): a question bank per version linked to clauses; a campaign asks the check with a pass
/// mark, attempts and drawn questions; a failed attempt names the sections to reread and never the answers; the last
/// failed attempt needs follow-up and tells the owner; the passing score stays with the attestation; answers are read
/// by compliance officers only, audited, never by line managers; packages without <c>policy.comprehension</c> and other
/// tenants reach nothing.
/// </summary>
[Collection(WorldCollection.Name)]
public sealed class PolicyCheckTests(TenantWorld world)
{
    private const string Policies = "/api/v1/policies";

    private async Task<TestTenant> FreshAsync(string package = "workplace_plus")
    {
        var t = await world.CreateTenantAsync("pch-" + Guid.NewGuid().ToString("N")[..8], package, "en", ["en", "ar"]);
        var holding = await world.CreateUnitAsync(t, "Holding", null, "organization");
        await world.CreateUnitAsync(t, "Gulf", holding, "subsidiary");
        foreach (var name in new[] { "co", "legal", "ali", "bea", "mona" })
        {
            await world.SeedUserAsync(t, name, MembershipTypes.Employee);
            await world.AddMembershipAsync(t, name, "Gulf");
        }
        await world.AssignAsync(t, "co", BuiltInRoles.ComplianceOfficer, "tenant", null);
        Assert.Equal(HttpStatusCode.OK, (await t["admin"].Client.PutAsJsonAsync($"/api/v1/people/{t["bea"].Id}/manager", new { managerId = t["mona"].Id })).StatusCode);
        return t;
    }

    private static async Task<JsonElement> OkAsync(Task<HttpResponseMessage> call, HttpStatusCode expected = HttpStatusCode.OK)
    {
        var response = await call;
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == expected, $"{(int)response.StatusCode}: {text}");
        return text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone();
    }

    private static async Task<string[]> ErrorKeysAsync(HttpResponseMessage response)
    {
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        if (body.TryGetProperty("errors", out var errors))
        {
            return errors.EnumerateObject().SelectMany(e => e.Value.EnumerateArray().Select(v => v.GetProperty("key").GetString()!)).ToArray();
        }
        return body.TryGetProperty("detailKey", out var detail) ? [detail.GetString()!] : [body.GetProperty("code").GetString()!];
    }

    private static Guid Id(JsonElement e) => e.GetProperty("id").GetGuid();

    private static Dictionary<string, string> Both(string text) => new() { ["en"] = text, ["ar"] = text + " ع" };

    private static object Clause(string text) => new
    {
        level = 1, titles = Both(text),
        bodies = new Dictionary<string, object>
        {
            ["en"] = new { blocks = new object[] { new { type = "paragraph", children = new[] { new { text = text + " text" } } } } },
            ["ar"] = new { blocks = new object[] { new { type = "paragraph", children = new[] { new { text = text + " نص" } } } } },
        },
    };

    /// <summary>A policy with the clauses Gifts and Hospitality in force, approved by legal; returns its ID, the version ID and the clause keys.</summary>
    private static async Task<(Guid Policy, Guid Version, string Gifts, string Hospitality)> PublishedAsync(TestTenant t)
    {
        var id = Id(await OkAsync(t["co"].Client.PostAsJsonAsync(Policies, new
        {
            code = "ABC", names = Both("Anti-bribery"), route = new[] { new { approvers = new[] { t["legal"].Id } } },
        }), HttpStatusCode.Created));
        var draft = await OkAsync(t["co"].Client.PutAsJsonAsync($"{Policies}/{id}/draft", new { titles = Both("Anti-bribery"), clauses = new[] { Clause("Gifts"), Clause("Hospitality") } }));
        await OkAsync(t["co"].Client.PostAsJsonAsync($"{Policies}/{id}/submit", new { }));
        var task = (await OkAsync(t["legal"].Client.GetAsync("/api/v1/tasks"))).EnumerateArray().Single(i => i.GetProperty("source").GetString() == "policy_approval");
        await OkAsync(t["legal"].Client.PostAsJsonAsync($"/api/v1/tasks/{task.GetProperty("taskId").GetGuid()}/decision", new { outcome = "approve" }));
        var clauses = draft.GetProperty("clauses").EnumerateArray().Select(c => c.GetProperty("key").GetString()!).ToArray();
        return (id, Id(draft), clauses[0], clauses[1]);
    }

    private static object Question(string clause, string text) => new
    {
        clauseKey = clause, prompts = Both(text), options = new[] { new { key = "a", texts = Both("Right") }, new { key = "b", texts = Both("Wrong") } }, correct = new[] { "a" },
    };

    private static Dictionary<Guid, string[]> Answers(JsonElement attempt, Func<JsonElement, string> choose) =>
        attempt.GetProperty("questions").EnumerateArray().ToDictionary(q => Id(q), q => new[] { choose(q) });

    [Fact]
    public async Task A_failed_attempt_names_what_to_reread_the_pass_is_kept_with_the_attestation_and_the_last_failure_needs_follow_up()
    {
        var t = await FreshAsync();
        var (policy, version, gifts, hospitality) = await PublishedAsync(t);
        var bank = $"{Policies}/{policy}/drafts/{version}/questions";
        Assert.True((await OkAsync(t["co"].Client.GetAsync($"{Policies}/{policy}"))).GetProperty("rights").GetProperty("comprehension").GetBoolean());

        // The bank is checked: a known clause, every language, two to six answers, one right answer and one wrong.
        Assert.Contains("validation.policies.check_clause", await ErrorKeysAsync(await t["co"].Client.PutAsJsonAsync(bank, new { questions = new[] { Question("nope", "Q") } })));
        Assert.Contains("validation.policies.check_prompt", await ErrorKeysAsync(await t["co"].Client.PutAsJsonAsync(bank, new
        {
            questions = new[] { new { clauseKey = gifts, prompts = new Dictionary<string, string> { ["en"] = "Only English" }, options = new[] { new { key = "a", texts = Both("A") }, new { key = "b", texts = Both("B") } }, correct = new[] { "a" } } },
        })));
        Assert.Contains("validation.policies.check_correct", await ErrorKeysAsync(await t["co"].Client.PutAsJsonAsync(bank, new
        {
            questions = new[] { new { clauseKey = gifts, prompts = Both("Q"), options = new[] { new { key = "a", texts = Both("A") }, new { key = "b", texts = Both("B") } }, correct = new[] { "a", "b" } } },
        })));
        Assert.Equal(HttpStatusCode.Forbidden, (await t["ali"].Client.PutAsJsonAsync(bank, new { questions = new[] { Question(gifts, "Q") } })).StatusCode);
        var saved = await OkAsync(t["co"].Client.PutAsJsonAsync(bank, new
        {
            questions = new[] { Question(gifts, "Gift limit?"), Question(gifts, "Who approves gifts?"), Question(hospitality, "Dinner with a supplier?") },
        }));
        Assert.Equal(3, saved.GetProperty("questions").GetArrayLength());
        Assert.Equal("1", saved.GetProperty("questions")[0].GetProperty("clauseNumber").GetString());
        await world.Database.AssertAuditedAsync(t.Id, AuditActions.PolicyCheckChanged, policy.ToString());

        // The campaign asks the check; the bank is then locked.
        Assert.Contains("validation.policies.check_bank", await ErrorKeysAsync(await t["co"].Client.PostAsJsonAsync($"{Policies}/{policy}/campaigns",
            new { check = new { passPercent = 100, attempts = 2, questions = 4 } })));
        var campaign = await OkAsync(t["co"].Client.PostAsJsonAsync($"{Policies}/{policy}/campaigns", new { check = new { passPercent = 100, attempts = 2 } }), HttpStatusCode.Created);
        var campaignId = Id(campaign);
        Assert.Equal(3, campaign.GetProperty("check").GetProperty("questions").GetInt32());
        Assert.Contains("conflict.policies.check_in_use", await ErrorKeysAsync(await t["co"].Client.PutAsJsonAsync(bank, new { questions = new[] { Question(gifts, "Q") } })));

        // Ali must pass first. The attempt shows the questions, never the right answers.
        var due = await OkAsync(t["ali"].Client.GetAsync($"{Policies}/{policy}/attestation"));
        Assert.Equal(2, due.GetProperty("check").GetProperty("attempts").GetInt32());
        Assert.False(due.GetProperty("check").GetProperty("passed").GetBoolean());
        Assert.Contains("conflict.policies.check_first", await ErrorKeysAsync(await t["ali"].Client.PostAsJsonAsync($"{Policies}/{policy}/attest", new { })));
        var started = await OkAsync(t["ali"].Client.PostAsJsonAsync($"{Policies}/{policy}/check/start", new { }));
        var attempt = started.GetProperty("current");
        Assert.Equal(3, attempt.GetProperty("questions").GetArrayLength());
        Assert.All(attempt.GetProperty("questions").EnumerateArray(), q => Assert.Equal(JsonValueKind.Null, q.GetProperty("correct").ValueKind));
        Assert.Equal(Id(attempt), Id((await OkAsync(t["ali"].Client.PostAsJsonAsync($"{Policies}/{policy}/check/start", new { }))).GetProperty("current")));
        var answers = $"{Policies}/{policy}/check/{Id(attempt)}/answers";
        Assert.Contains("validation.policies.check_answers", await ErrorKeysAsync(await t["ali"].Client.PostAsJsonAsync(answers, new { answers = new Dictionary<Guid, string[]>() })));

        // Wrong on hospitality: below the mark, reread Hospitality only, one attempt left.
        var failed = await OkAsync(t["ali"].Client.PostAsJsonAsync(answers, new { answers = Answers(attempt, q => q.GetProperty("clauseKey").GetString() == hospitality ? "b" : "a") }));
        Assert.False(failed.GetProperty("passed").GetBoolean());
        Assert.Equal(66, failed.GetProperty("score").GetInt32());
        Assert.Equal(1, failed.GetProperty("attemptsLeft").GetInt32());
        var reread = Assert.Single(failed.GetProperty("reread").EnumerateArray());
        Assert.Equal(hospitality, reread.GetProperty("key").GetString());
        Assert.Equal("2", reread.GetProperty("number").GetString());
        Assert.DoesNotContain("correct\":[", failed.GetRawText());
        Assert.Contains("conflict.policies.check_answered", await ErrorKeysAsync(await t["ali"].Client.PostAsJsonAsync(answers, new { answers = Answers(attempt, _ => "a") })));

        // Second attempt right: Ali attests and the record keeps the score.
        var second = (await OkAsync(t["ali"].Client.PostAsJsonAsync($"{Policies}/{policy}/check/start", new { }))).GetProperty("current");
        var passed = await OkAsync(t["ali"].Client.PostAsJsonAsync($"{Policies}/{policy}/check/{Id(second)}/answers", new { answers = Answers(second, _ => "a") }));
        Assert.True(passed.GetProperty("passed").GetBoolean());
        Assert.Empty(passed.GetProperty("reread").EnumerateArray());
        Assert.Contains("conflict.policies.check_passed", await ErrorKeysAsync(await t["ali"].Client.PostAsJsonAsync($"{Policies}/{policy}/check/start", new { })));
        await OkAsync(t["ali"].Client.PostAsJsonAsync($"{Policies}/{policy}/attest", new { }), HttpStatusCode.Created);
        Assert.Equal(1, await world.Database.CountInTenantAsync(t.Id,
            "select count(*) from policies.attestation where user_id = @u and (comprehension->>'score')::int = 100 and (comprehension->>'attempt')::int = 2", ("u", t["ali"].Id)));

        // Bea fails both: she needs follow-up, the owner hears of it, and she may not try again.
        for (var i = 0; i < 2; i++)
        {
            var a = (await OkAsync(t["bea"].Client.PostAsJsonAsync($"{Policies}/{policy}/check/start", new { }))).GetProperty("current");
            var result = await OkAsync(t["bea"].Client.PostAsJsonAsync($"{Policies}/{policy}/check/{Id(a)}/answers", new { answers = Answers(a, _ => "b") }));
            Assert.Equal(i == 1, result.GetProperty("needsFollowUp").GetBoolean());
        }
        Assert.Contains("conflict.policies.check_no_attempts", await ErrorKeysAsync(await t["bea"].Client.PostAsJsonAsync($"{Policies}/{policy}/check/start", new { })));
        Assert.Equal(1, await world.Database.CountInTenantAsync(t.Id, "select count(*) from notifications.notification where user_id = @u and template_key = @k",
            ("u", t["co"].Id), ("k", PolicyChecks.FollowUpTemplate)));
        await world.Database.AssertAuditedAsync(t.Id, AuditActions.PolicyCheckFailed, policy.ToString());
        var people = await OkAsync(t["co"].Client.GetAsync($"{Policies}/{policy}/compliance/people?campaign={campaignId}"));
        Assert.True(people.GetProperty("items").EnumerateArray().Single(p => p.GetProperty("person").GetProperty("id").GetGuid() == t["bea"].Id).GetProperty("needsFollowUp").GetBoolean());
        var managers = await OkAsync(t["mona"].Client.GetAsync($"{Policies}/{policy}/compliance/people?campaign={campaignId}"));
        Assert.True(Assert.Single(managers.GetProperty("items").EnumerateArray()).GetProperty("needsFollowUp").GetBoolean());

        // Answers: the line manager is refused, the compliance officer reads them and the read is audited; then gives the attempts back.
        var memberPath = $"{Policies}/{policy}/campaigns/{campaignId}/checks/{t["bea"].Id}";
        Assert.Equal(HttpStatusCode.Forbidden, (await t["mona"].Client.GetAsync(memberPath)).StatusCode);
        var member = await OkAsync(t["co"].Client.GetAsync(memberPath));
        Assert.Equal(2, member.GetProperty("attempts").GetArrayLength());
        Assert.Equal(0, member.GetProperty("attempts")[0].GetProperty("score").GetInt32());
        await world.Database.AssertAuditedAsync(t.Id, AuditActions.PolicyCheckViewed, policy.ToString());
        Assert.Contains("conflict.policies.check_not_followed", await ErrorKeysAsync(await t["co"].Client.PostAsJsonAsync(
            $"{Policies}/{policy}/campaigns/{campaignId}/checks/{t["ali"].Id}/reset", new { })));
        var reset = await OkAsync(t["co"].Client.PostAsJsonAsync($"{memberPath}/reset", new { }));
        Assert.Equal(4, reset.GetProperty("due").GetProperty("attempts").GetInt32());
        Assert.False(reset.GetProperty("due").GetProperty("needsFollowUp").GetBoolean());
        await world.Database.AssertAuditedAsync(t.Id, AuditActions.PolicyCheckReset, policy.ToString());
        var third = (await OkAsync(t["bea"].Client.PostAsJsonAsync($"{Policies}/{policy}/check/start", new { }))).GetProperty("current");
        Assert.Equal(3, third.GetProperty("attempt").GetInt32());

        // Another tenant finds nothing.
        var other = await world.CreateTenantAsync("pcx-" + Guid.NewGuid().ToString("N")[..8], "workplace_plus", "en", ["en"]);
        await world.SeedUserAsync(other, "co", MembershipTypes.Employee);
        await world.AssignAsync(other, "co", BuiltInRoles.ComplianceOfficer, "tenant", null);
        Assert.Equal(HttpStatusCode.NotFound, (await other["co"].Client.GetAsync(bank)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other["co"].Client.GetAsync(memberPath)).StatusCode);
    }

    [Fact]
    public async Task Without_policy_comprehension_there_is_no_bank_and_no_campaign_with_a_check()
    {
        var t = await FreshAsync("workplace");
        var (policy, version, gifts, _) = await PublishedAsync(t);
        Assert.False((await OkAsync(t["co"].Client.GetAsync($"{Policies}/{policy}"))).GetProperty("rights").GetProperty("comprehension").GetBoolean());
        Assert.Equal(HttpStatusCode.Forbidden, (await t["co"].Client.PutAsJsonAsync($"{Policies}/{policy}/drafts/{version}/questions",
            new { questions = new[] { Question(gifts, "Q") } })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await t["co"].Client.PostAsJsonAsync($"{Policies}/{policy}/campaigns", new { check = new { passPercent = 80 } })).StatusCode);
        // A campaign without a check still works, and the person attests directly.
        await OkAsync(t["co"].Client.PostAsJsonAsync($"{Policies}/{policy}/campaigns", new { }), HttpStatusCode.Created);
        Assert.Equal(JsonValueKind.Null, (await OkAsync(t["ali"].Client.GetAsync($"{Policies}/{policy}/attestation"))).GetProperty("check").ValueKind);
        await OkAsync(t["ali"].Client.PostAsJsonAsync($"{Policies}/{policy}/attest", new { }), HttpStatusCode.Created);
    }
}
