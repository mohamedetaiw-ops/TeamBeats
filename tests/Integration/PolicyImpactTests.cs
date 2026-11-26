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
/// Spec 025 R3 T024 (US9, FR-022, FR-023): before approving, the people who may compare versions see what a change
/// touches. The facts come from the register (changed clauses, attestations of the version in force, open exemptions,
/// questions on changed clauses, linked controls, related policies, onboarding). The AI assessment goes through the AI
/// Gateway, keeps only the controls and policies it was given, is kept per working version and locale, and is audited.
/// A confidential policy keeps its facts but goes to no model; employees and other tenants reach nothing; a first version
/// has nothing to compare. Names are fictional.
/// </summary>
[Collection(WorldCollection.Name)]
public sealed class PolicyImpactTests(TenantWorld world)
{
    private const string Policies = "/api/v1/policies";

    private async Task<TestTenant> FreshAsync()
    {
        var t = await world.CreateTenantAsync("pim-" + Guid.NewGuid().ToString("N")[..8], "enterprise_gov", "en", ["en", "ar"]);
        var holding = await world.CreateUnitAsync(t, "Holding", null, "organization");
        await world.CreateUnitAsync(t, "Gulf", holding, "subsidiary");
        foreach (var name in new[] { "co", "legal", "ali" })
        {
            await world.SeedUserAsync(t, name, MembershipTypes.Employee);
            await world.AddMembershipAsync(t, name, "Gulf");
        }
        await world.AssignAsync(t, "co", BuiltInRoles.ComplianceOfficer, "tenant", null);
        return t;
    }

    private static async Task<JsonElement> OkAsync(Task<HttpResponseMessage> call, HttpStatusCode expected = HttpStatusCode.OK)
    {
        var response = await call;
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == expected, $"{(int)response.StatusCode}: {text}");
        return text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone();
    }

    private static Dictionary<string, string> Both(string text) => new() { ["en"] = text, ["ar"] = text + " ع" };

    private static object Clause(string? key, string text) => new
    {
        key, level = 1, titles = Both(text),
        bodies = new Dictionary<string, object>
        {
            ["en"] = new { blocks = new object[] { new { type = "paragraph", children = new[] { new { text = text + " text." } } } } },
            ["ar"] = new { blocks = new object[] { new { type = "paragraph", children = new[] { new { text = text + " نص." } } } } },
        },
    };

    private static async Task DecideAsync(TestTenant t)
    {
        var task = (await OkAsync(t["legal"].Client.GetAsync("/api/v1/tasks"))).EnumerateArray().Single(i => i.GetProperty("source").GetString() == "policy_approval");
        await OkAsync(t["legal"].Client.PostAsJsonAsync($"/api/v1/tasks/{task.GetProperty("taskId").GetGuid()}/decision", new { outcome = "approve" }));
    }

    private sealed record Revised(Guid Policy, Guid Related);

    /// <summary>
    /// Version 1 (Scope, Gifts) in force with a question on Gifts and Ali's attestation, two linked controls and one
    /// related policy; version 2 in review changes Scope, drops Gifts and adds Email.
    /// </summary>
    private static async Task<Revised> RevisedAsync(TestTenant t, string classification = "internal", Func<Guid, Task>? beforeFirst = null)
    {
        var related = (await OkAsync(t["co"].Client.PostAsJsonAsync(Policies, new
        {
            code = "GIFT", names = Both("Gifts"), route = new[] { new { approvers = new[] { t["legal"].Id } } },
        }), HttpStatusCode.Created)).GetProperty("id").GetGuid();
        var id = (await OkAsync(t["co"].Client.PostAsJsonAsync(Policies, new
        {
            code = "AUP", names = Both("AUP"), classification, route = new[] { new { approvers = new[] { t["legal"].Id } } }, related = new[] { related },
        }), HttpStatusCode.Created)).GetProperty("id").GetGuid();
        await OkAsync(t["co"].Client.PutAsJsonAsync($"{Policies}/{id}/controls", new
        {
            controls = new[] { new { framework = "iso27001", code = "5.10" }, new { framework = "iso27001", code = "5.1" } },
        }));
        var first = await OkAsync(t["co"].Client.PutAsJsonAsync($"{Policies}/{id}/draft", new { titles = Both("AUP"), clauses = new[] { Clause(null, "Scope"), Clause(null, "Gifts") } }));
        var keys = first.GetProperty("clauses").EnumerateArray().Select(c => c.GetProperty("key").GetString()!).ToArray();
        if (beforeFirst is not null)
        {
            await beforeFirst(id);
        }
        await OkAsync(t["co"].Client.PostAsJsonAsync($"{Policies}/{id}/submit", new { }));
        await DecideAsync(t);
        await OkAsync(t["co"].Client.PutAsJsonAsync($"{Policies}/{id}/drafts/{first.GetProperty("id").GetGuid()}/questions", new
        {
            questions = new[]
            {
                new { clauseKey = keys[1], prompts = Both("Gift limit?"), options = new[] { new { key = "a", texts = Both("Right") }, new { key = "b", texts = Both("Wrong") } }, correct = new[] { "a" } },
            },
        }));
        await OkAsync(t["co"].Client.PostAsJsonAsync($"{Policies}/{id}/campaigns", new { }), HttpStatusCode.Created);
        await OkAsync(t["ali"].Client.PostAsJsonAsync($"{Policies}/{id}/attest", new { }), HttpStatusCode.Created);
        await OkAsync(t["co"].Client.PutAsJsonAsync($"{Policies}/{id}/draft", new { titles = Both("AUP"), clauses = new[] { Clause(keys[0], "Scope of use"), Clause(null, "Email") } }));
        await OkAsync(t["co"].Client.PostAsJsonAsync($"{Policies}/{id}/submit", new { }));
        return new Revised(id, related);
    }

    [Fact]
    public async Task Approvers_see_the_facts_of_a_change_and_one_kept_ai_assessment_of_what_it_touches()
    {
        var t = await FreshAsync();
        var r = await RevisedAsync(t, beforeFirst: async id =>
        {
            // A first version has nothing to compare with, and no model is asked.
            var none = await OkAsync(t["co"].Client.PostAsync($"{Policies}/{id}/impact/assessment", null));
            Assert.Equal("nothing_to_compare", none.GetProperty("outcome").GetString());
            Assert.Equal(JsonValueKind.Null, none.GetProperty("assessment").ValueKind);
        });
        var url = $"{Policies}/{r.Policy}/impact";

        // The facts are there before anyone asks a model.
        var facts = await OkAsync(t["legal"].Client.GetAsync(url));
        Assert.Equal("not_assessed", facts.GetProperty("outcome").GetString());
        var f = facts.GetProperty("facts");
        Assert.Equal(1, f.GetProperty("base").GetInt32());
        Assert.Equal(["added", "changed", "removed"], f.GetProperty("clauses").EnumerateArray().Select(c => c.GetProperty("change").GetString()!).Order().ToArray());
        Assert.Equal(1, f.GetProperty("attestedCurrent").GetInt32());
        Assert.Equal(0, f.GetProperty("openExemptions").GetInt32());
        var question = Assert.Single(f.GetProperty("questions").EnumerateArray());
        Assert.Equal("2", question.GetProperty("clauseNumber").GetString());
        Assert.Equal(2, f.GetProperty("controls").GetArrayLength());
        Assert.Equal(r.Related, Assert.Single(f.GetProperty("related").EnumerateArray()).GetProperty("id").GetGuid());
        Assert.False(f.GetProperty("onboardingRequired").GetBoolean());

        // The assessment is labelled, names only what it was given, is kept and audited.
        var made = await OkAsync(t["legal"].Client.PostAsync($"{url}/assessment", null));
        Assert.Equal("assessed", made.GetProperty("outcome").GetString());
        var a = made.GetProperty("assessment");
        Assert.True(a.GetProperty("aiGenerated").GetBoolean());
        Assert.Equal("Fake impact: 3 clauses reach 2 controls and 1 policies.", a.GetProperty("summary").GetString());
        var impacts = a.GetProperty("impacts").EnumerateArray().ToList();
        Assert.Equal(3, impacts.Count);
        Assert.All(impacts, i =>
        {
            Assert.Equal(["iso27001:5.1", "iso27001:5.10"], i.GetProperty("controls").EnumerateArray().Select(c => c.GetString()!).Order().ToArray());
            Assert.Equal([r.Related], i.GetProperty("policies").EnumerateArray().Select(p => p.GetGuid()).ToArray());
        });
        await world.Database.AssertAuditedAsync(t.Id, AuditActions.PolicyImpactAssessed, r.Policy.ToString());
        var kept = await OkAsync(t["co"].Client.GetAsync(url));
        Assert.Equal("assessed", kept.GetProperty("outcome").GetString());
        Assert.Equal(a.GetProperty("generatedAt").GetDateTimeOffset(), kept.GetProperty("assessment").GetProperty("generatedAt").GetDateTimeOffset());
        Assert.Equal(a.GetProperty("generatedAt").GetDateTimeOffset(), (await OkAsync(t["co"].Client.PostAsync($"{url}/assessment", null))).GetProperty("assessment").GetProperty("generatedAt").GetDateTimeOffset());
        Assert.Equal(1L, await world.Database.CountInTenantAsync(t.Id, "select count(*) from policies.impact_assessment"));

        // Employees and other tenants reach nothing.
        Assert.Equal(HttpStatusCode.NotFound, (await t["ali"].Client.GetAsync(url)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await t["ali"].Client.PostAsync($"{url}/assessment", null)).StatusCode);
        var other = await FreshAsync();
        Assert.Equal(HttpStatusCode.NotFound, (await other["co"].Client.GetAsync(url)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other["co"].Client.PostAsync($"{url}/assessment", null)).StatusCode);
    }

    [Fact]
    public async Task A_confidential_policy_keeps_its_facts_but_goes_to_no_model()
    {
        var t = await FreshAsync();
        var r = await RevisedAsync(t, "confidential");
        var blocked = await OkAsync(t["legal"].Client.PostAsync($"{Policies}/{r.Policy}/impact/assessment", null));
        Assert.Equal("policy_blocked", blocked.GetProperty("outcome").GetString());
        Assert.Equal(JsonValueKind.Null, blocked.GetProperty("assessment").ValueKind);
        Assert.Equal(3, blocked.GetProperty("facts").GetProperty("clauses").GetArrayLength());
        Assert.Equal(0L, await world.Database.CountInTenantAsync(t.Id, "select count(*) from policies.impact_assessment"));
        await world.Database.AssertAuditedAsync(t.Id, AuditActions.PolicyImpactAssessed, r.Policy.ToString());
    }
}
