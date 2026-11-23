using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using TeamBeats.Authorization.Contracts;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Testing;
using Xunit;

namespace TeamBeats.Integration.Tests;

/// <summary>
/// Spec 025 T020 (section 11): approvers get an AI change summary of the working version against the version in force,
/// through the AI Gateway, labelled as AI-generated, beside the compare view. It is kept per working version and locale,
/// so every approver reads the same one, in the reader's language. A confidential policy does not go to a model under the
/// default AI policy; employees and other tenants reach nothing; a first version has nothing to compare.
/// </summary>
[Collection(WorldCollection.Name)]
public sealed class PolicyChangeSummaryTests(TenantWorld world)
{
    private const string Policies = "/api/v1/policies";

    private async Task<TestTenant> FreshAsync()
    {
        var t = await world.CreateTenantAsync("pcs-" + Guid.NewGuid().ToString("N")[..8], "workplace_plus", "en", ["en", "ar"]);
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

    /// <summary>Version 1 with two clauses in force, and version 2 in review that changes one, drops one and adds one.</summary>
    private static async Task<Guid> RevisedAsync(TestTenant t, string classification = "internal", string added = "Email")
    {
        var id = (await OkAsync(t["co"].Client.PostAsJsonAsync(Policies, new
        {
            code = "AUP", names = Both("AUP"), classification, route = new[] { new { approvers = new[] { t["legal"].Id } } },
        }), HttpStatusCode.Created)).GetProperty("id").GetGuid();
        var first = await OkAsync(t["co"].Client.PutAsJsonAsync($"{Policies}/{id}/draft", new { titles = Both("AUP"), clauses = new[] { Clause(null, "Scope"), Clause(null, "Gifts") } }));
        var keys = first.GetProperty("clauses").EnumerateArray().Select(c => c.GetProperty("key").GetString()).ToArray();
        // A first version has nothing to compare with, and no model is asked.
        Assert.Equal("nothing_to_compare", (await OkAsync(t["co"].Client.PostAsync($"{Policies}/{id}/compare/summary", null))).GetProperty("outcome").GetString());
        await OkAsync(t["co"].Client.PostAsJsonAsync($"{Policies}/{id}/submit", new { }));
        await DecideAsync(t);
        await OkAsync(t["co"].Client.PutAsJsonAsync($"{Policies}/{id}/draft", new { titles = Both("AUP"), clauses = new[] { Clause(keys[0], "Scope of use"), Clause(null, added) } }));
        await OkAsync(t["co"].Client.PostAsJsonAsync($"{Policies}/{id}/submit", new { }));
        return id;
    }

    [Fact]
    public async Task Approvers_get_one_kept_ai_summary_of_the_changes_beside_the_compare_view()
    {
        var t = await FreshAsync();
        var policy = await RevisedAsync(t);
        var url = $"{Policies}/{policy}/compare/summary";

        // Nothing is asked until someone asks; then the approver reads a labelled summary that names each change.
        Assert.Equal("unavailable", (await OkAsync(t["legal"].Client.GetAsync(url))).GetProperty("outcome").GetString());
        var made = await OkAsync(t["legal"].Client.PostAsync(url, null));
        Assert.Equal("summarized", made.GetProperty("outcome").GetString());
        var summary = made.GetProperty("summary");
        Assert.True(summary.GetProperty("aiGenerated").GetBoolean());
        Assert.Equal("en", summary.GetProperty("locale").GetString());
        Assert.Equal("Fake summary: 3 changes.", summary.GetProperty("summary").GetString());
        Assert.Equal(["Clause 1: changed", "Clause 2: added", "Clause 2: removed"], summary.GetProperty("points").EnumerateArray().Select(p => p.GetString()!).ToArray());

        // The owner reads the same kept summary without a new call; in Arabic it is a summary of its own.
        var kept = await OkAsync(t["co"].Client.GetAsync(url));
        Assert.Equal(summary.GetProperty("generatedAt").GetDateTimeOffset(), kept.GetProperty("summary").GetProperty("generatedAt").GetDateTimeOffset());
        Assert.Equal(summary.GetProperty("generatedAt").GetDateTimeOffset(), (await OkAsync(t["co"].Client.PostAsync(url, null))).GetProperty("summary").GetProperty("generatedAt").GetDateTimeOffset());
        (await t["legal"].Client.PutAsJsonAsync("/api/v1/me/preferences", new { locale = "ar" })).EnsureSuccessStatusCode();
        var arabic = await OkAsync(t["legal"].Client.PostAsync(url, null));
        Assert.Equal("ar", arabic.GetProperty("summary").GetProperty("locale").GetString());
        Assert.StartsWith("ملخص تجريبي", arabic.GetProperty("summary").GetProperty("summary").GetString());
        Assert.Equal(2L, await world.Database.CountInTenantAsync(t.Id, "select count(*) from policies.change_summary"));

        // The compare view stays as it was.
        Assert.Equal(3, (await OkAsync(t["legal"].Client.GetAsync($"{Policies}/{policy}/compare"))).GetProperty("clauses").GetArrayLength());

        // Employees and other tenants reach nothing.
        Assert.Equal(HttpStatusCode.NotFound, (await t["ali"].Client.PostAsync(url, null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await t["ali"].Client.GetAsync(url)).StatusCode);
        var other = await FreshAsync();
        Assert.Equal(HttpStatusCode.NotFound, (await other["co"].Client.PostAsync(url, null)).StatusCode);
    }

    [Fact]
    public async Task A_confidential_policy_does_not_go_to_a_model_and_nothing_is_kept()
    {
        var t = await FreshAsync();
        var policy = await RevisedAsync(t, "confidential");
        var blocked = await OkAsync(t["legal"].Client.PostAsync($"{Policies}/{policy}/compare/summary", null));
        Assert.Equal("policy_blocked", blocked.GetProperty("outcome").GetString());
        Assert.Equal(JsonValueKind.Null, blocked.GetProperty("summary").ValueKind);
        Assert.Equal(0L, await world.Database.CountInTenantAsync(t.Id, "select count(*) from policies.change_summary"));
    }

    [Fact]
    public async Task An_answer_that_is_not_a_summary_is_unavailable_and_nothing_is_kept()
    {
        var t = await FreshAsync();
        var policy = await RevisedAsync(t, added: "Email [fake:badjson]");
        Assert.Equal("unavailable", (await OkAsync(t["legal"].Client.PostAsync($"{Policies}/{policy}/compare/summary", null))).GetProperty("outcome").GetString());
        Assert.Equal(0L, await world.Database.CountInTenantAsync(t.Id, "select count(*) from policies.change_summary"));
    }
}
