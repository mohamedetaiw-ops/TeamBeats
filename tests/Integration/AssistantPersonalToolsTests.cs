using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TeamBeats.Authorization.Contracts;
using TeamBeats.Modules.Agents.Contracts;
using TeamBeats.Modules.Hr.Leave;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Testing;
using Xunit;

namespace TeamBeats.Integration.Tests;

/// <summary>
/// Spec 014 R2 slice 2 (T011, T012, US7): the "My HR helper" template answers questions about the person's own leave
/// through self-scoped tools that read only their records and show the source; a question about someone else is
/// refused unless the caller is an HR officer covering that person (spec 004). Nothing crosses tenants.
/// </summary>
[Collection(WorldCollection.Name)]
public sealed class AssistantPersonalToolsTests(TenantWorld world)
{
    private const string Agents = "/api/v1/agents";
    private const string Leave = "/api/v1/hr/leave";

    private static async Task<JsonElement> OkAsync(Task<HttpResponseMessage> call, HttpStatusCode expected = HttpStatusCode.OK)
    {
        var response = await call;
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == expected, $"{(int)response.StatusCode}: {text}");
        return text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone();
    }

    private async Task<TestTenant> FreshAsync()
    {
        var t = await world.CreateTenantAsync("ph-" + Guid.NewGuid().ToString("N")[..8], "workplace_plus", "en", ["en", "ar"]);
        var hr = await world.CreateUnitAsync(t, "Hr", null, "subsidiary");
        foreach (var name in new[] { "sara", "emp", "peer", "hro", "aip" })
        {
            await world.SeedUserAsync(t, name, MembershipTypes.Employee);
            await world.AddMembershipAsync(t, name, "Hr");
        }
        await world.AssignAsync(t, "sara", BuiltInRoles.AgentDesigner, "org_subtree", hr);
        await world.AssignAsync(t, "aip", BuiltInRoles.AiPolicyAdministrator, "tenant", null);
        await world.AssignAsync(t, "hro", BuiltInRoles.HrOfficer, "org_subtree", hr);

        await OkAsync(t["admin"].Client.PutAsJsonAsync($"{Leave}/types/annual", new { names = new Dictionary<string, string> { ["en"] = "Annual leave", ["ar"] = "إجازة سنوية" } }));
        await OkAsync(t["admin"].Client.PostAsJsonAsync($"{Leave}/policies", new { typeKey = "annual", bands = new[] { new { days = 21m } }, accrual = "yearly" }),
            HttpStatusCode.Created);
        await world.Factory.InScopeAsync(t.Id, async sp =>
        {
            var written = await sp.GetRequiredService<LeaveLedger>().RunAccrualAsync(default);
            await sp.GetRequiredService<IDbSession>().CommitAsync();
            return written;
        });
        return t;
    }

    /// <summary>Installs the template in Hr as Sara, submits it and has the AI policy administrator publish it.</summary>
    private async Task<Guid> PublishedAsync(TestTenant t)
    {
        var detail = await OkAsync(t["sara"].Client.PostAsJsonAsync(Agents, new { templateId = "my_hr_helper", orgUnitId = t.Unit("Hr") }), HttpStatusCode.Created);
        var id = detail.GetProperty("agent").GetProperty("id").GetGuid();
        await OkAsync(t["sara"].Client.PostAsJsonAsync($"{Agents}/{id}/submit", new { revision = 1 }));
        await world.Database.PassGateAsync(t.Id, id, 1);
        await OkAsync(t["aip"].Client.PostAsJsonAsync($"{Agents}/{id}/publish", new { version = 1 }));
        return id;
    }

    private static string Call(string tool, string? args = null) => $"[fake:tool:{tool.Replace(".", "__")}]" + (args is null ? string.Empty : $" [fake:args:{args}]");

    private static Task<JsonElement> AskAsync(HttpClient client, string question) =>
        OkAsync(client.PostAsJsonAsync("/api/v1/assistant/turns", new { question }));

    private static Task<JsonElement> RecordAsync(HttpClient client, JsonElement turn) =>
        OkAsync(client.GetAsync($"{Agents}/runs/{turn.GetProperty("run").GetProperty("id").GetGuid()}"));

    private static async Task<List<(string Kind, string? Tool, string Decision, string? Reason)>> StepsAsync(HttpClient client, JsonElement turn)
    {
        var record = await RecordAsync(client, turn);
        return record.GetProperty("steps").EnumerateArray().Select(s => (s.GetProperty("kind").GetString()!, s.GetProperty("tool").GetString(),
            s.GetProperty("decision").GetString()!, s.GetProperty("reason").GetString())).ToList();
    }

    [Fact]
    public async Task My_HR_helper_reads_only_the_persons_own_leave_and_cites_it()
    {
        var t = await FreshAsync();
        var agent = await PublishedAsync(t);

        // The question routes to the helper and the balance tool reads the person's own record.
        var turn = await AskAsync(t["emp"].Client, "How many leave days do I have? [fake:route:1] " + Call(LeaveBalanceTool.Name));
        Assert.Equal(AssistantOutcomes.Answered, turn.GetProperty("outcome").GetString());
        Assert.Equal(agent, turn.GetProperty("answeredBy").GetProperty("id").GetGuid());
        Assert.Contains(("tool", LeaveBalanceTool.Name, "executed", null), await StepsAsync(t["emp"].Client, turn));
        // The execution record keeps the source the tool read, which the answer cites.
        var citation = Assert.Single((await RecordAsync(t["emp"].Client, turn)).GetProperty("sources").EnumerateArray());
        Assert.Equal(LeaveBalanceTool.Source, citation.GetProperty("source").GetString());
        Assert.Equal(t["emp"].Id, citation.GetProperty("itemId").GetGuid());
        Assert.Equal("/leave", citation.GetProperty("link").GetString());

        // Open leave comes from the requests tool.
        var open = await AskAsync(t["emp"].Client, "Which leave requests do I have open? [fake:route:1] " + Call(MyLeaveRequestsTool.Name));
        Assert.Contains(("tool", MyLeaveRequestsTool.Name, "executed", null), await StepsAsync(t["emp"].Client, open));

        // Another person's balance is refused for a colleague, and the refusal does not say whether they exist.
        var other = await AskAsync(t["peer"].Client, "What is my colleague's leave balance? [fake:route:1] " + Call(LeaveBalanceTool.Name, $$"""{"person":"{{t["emp"].Id}}"}"""));
        Assert.Contains(("tool", LeaveBalanceTool.Name, "refused", "tool.other_person"), await StepsAsync(t["peer"].Client, other));
        var nobody = await AskAsync(t["peer"].Client, "And this person? [fake:route:1] " + Call(LeaveBalanceTool.Name, $$"""{"person":"{{Guid.NewGuid()}}"}"""));
        Assert.Contains(("tool", LeaveBalanceTool.Name, "refused", "tool.other_person"), await StepsAsync(t["peer"].Client, nobody));

        // An HR officer covering the person may read it under spec 004.
        var hr = await AskAsync(t["hro"].Client, "What is my colleague's leave balance? [fake:route:1] " + Call(LeaveBalanceTool.Name, $$"""{"person":"{{t["emp"].Id}}"}"""));
        Assert.Contains(("tool", LeaveBalanceTool.Name, "executed", null), await StepsAsync(t["hro"].Client, hr));
        Assert.Equal(t["emp"].Id, Assert.Single((await RecordAsync(t["hro"].Client, hr)).GetProperty("sources").EnumerateArray()).GetProperty("itemId").GetGuid());

        // Another tenant's HR officer never reaches the person.
        var elsewhere = await FreshAsync();
        await PublishedAsync(elsewhere);
        var cross = await AskAsync(elsewhere["hro"].Client, "Balance? [fake:route:1] " + Call(LeaveBalanceTool.Name, $$"""{"person":"{{t["emp"].Id}}"}"""));
        Assert.Contains(("tool", LeaveBalanceTool.Name, "refused", "tool.other_person"), await StepsAsync(elsewhere["hro"].Client, cross));
    }
}
