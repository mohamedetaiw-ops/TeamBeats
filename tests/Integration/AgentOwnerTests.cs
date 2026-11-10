using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TeamBeats.Audit.Contracts;
using TeamBeats.Authorization.Contracts;
using TeamBeats.Messaging;
using TeamBeats.Modules.Identity.Contracts;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Testing;
using Xunit;

namespace TeamBeats.Integration.Tests;

/// <summary>
/// Agent owners (spec 020 FR-002 and the edge case "agent owner leaves the company"): the designer names an owner and a
/// backup owner who are active members, the business purpose is part of the definition, and when the owner leaves the
/// backup owner or an AI Policy Administrator takes over, marked and audited, while the agent keeps running. Nothing
/// crosses tenants.
/// </summary>
[Collection(WorldCollection.Name)]
public sealed class AgentOwnerTests(TenantWorld world)
{
    private const string Agents = "/api/v1/agents";

    private async Task<TestTenant> FreshAsync()
    {
        var t = await world.CreateTenantAsync("ao-" + Guid.NewGuid().ToString("N")[..8], "workplace_plus", "en", ["en", "ar"]);
        foreach (var name in new[] { "sara", "bo", "emp", "aip" })
        {
            await world.SeedUserAsync(t, name, MembershipTypes.Employee);
        }
        var hr = await world.CreateUnitAsync(t, "Hr", null, "subsidiary");
        await world.AddMembershipAsync(t, "emp", "Hr");
        await world.AssignAsync(t, "sara", BuiltInRoles.AgentDesigner, "org_subtree", hr);
        await world.AssignAsync(t, "bo", BuiltInRoles.AgentDesigner, "org_subtree", hr);
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

    private static async Task<string> ProblemAsync(Task<HttpResponseMessage> call, HttpStatusCode expected)
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

    private async Task<Guid> PublishedAsync(TestTenant t)
    {
        var detail = await OkAsync(t["sara"].Client.PostAsJsonAsync(Agents, new { templateId = "enterprise_knowledge", orgUnitId = t.Unit("Hr") }), HttpStatusCode.Created);
        var id = detail.GetProperty("agent").GetProperty("id").GetGuid();
        var definition = JsonSerializer.Deserialize<Dictionary<string, object?>>(detail.GetProperty("definition").GetRawText())!;
        definition["purpose"] = new Dictionary<string, string> { ["en"] = "Answer policy questions for the HR unit.", ["ar"] = "الإجابة عن أسئلة السياسات لوحدة الموارد البشرية." };
        var saved = await OkAsync(t["sara"].Client.PutAsJsonAsync($"{Agents}/{id}/draft", new { revision = 1, definition }));
        Assert.Equal("Answer policy questions for the HR unit.", saved.GetProperty("definition").GetProperty("purpose").GetProperty("en").GetString());
        await OkAsync(t["sara"].Client.PostAsJsonAsync($"{Agents}/{id}/submit", new { revision = 2 }));
        await world.Database.PassGateAsync(t.Id, id, 1);
        await OkAsync(t["aip"].Client.PostAsJsonAsync($"{Agents}/{id}/publish", new { version = 1 }));
        return id;
    }

    private Task<long> OwnerAuditsAsync(TestTenant t) =>
        world.Database.CountInTenantAsync(t.Id, "select count(*) from audit.audit_event where action = @a", ("a", AuditActions.AgentOwnerChanged));

    private async Task LeavesAsync(TestTenant t, string name)
    {
        await world.Factory.InScopeAsync(t.Id, async sp =>
        {
            await sp.GetRequiredService<IOutbox>().EnqueueAsync(new UserDeprovisioned(t[name].Id, "directory"));
            await sp.GetRequiredService<IDbSession>().CommitAsync();
            return true;
        });
        var dispatcher = world.Factory.Services.GetRequiredService<OutboxDispatcher>();
        for (var i = 0; i < 20 && await dispatcher.DispatchBatchAsync(500, CancellationToken.None) > 0; i++)
        {
        }
    }

    private static async Task<string[]> NotificationLinksAsync(HttpClient client) =>
        (await OkAsync(client.GetAsync("/api/v1/me/notifications"))).GetProperty("items").EnumerateArray().Select(n => n.GetProperty("link").GetString()!).ToArray();

    [Fact]
    public async Task The_designer_names_an_owner_and_a_backup_owner_and_the_purpose_is_checked()
    {
        var t = await FreshAsync();
        var id = await PublishedAsync(t);

        var set = await OkAsync(t["sara"].Client.PutAsJsonAsync($"{Agents}/{id}/owners", new { ownerUserId = t["sara"].Id, backupOwnerUserId = t["bo"].Id }));
        Assert.Equal(t["bo"].Id, set.GetProperty("agent").GetProperty("backupOwnerUserId").GetGuid());
        Assert.Equal(1, await OwnerAuditsAsync(t));

        // The backup owner sees the agent; the same person twice, or someone outside the tenant, is refused.
        await OkAsync(t["bo"].Client.GetAsync($"{Agents}/{id}"));
        Assert.Equal("validation.agent.backup_owner", await ProblemAsync(t["sara"].Client.PutAsJsonAsync($"{Agents}/{id}/owners",
            new { ownerUserId = t["sara"].Id, backupOwnerUserId = t["sara"].Id }), HttpStatusCode.BadRequest));
        var other = await FreshAsync();
        Assert.Equal("validation.agent.owner", await ProblemAsync(t["sara"].Client.PutAsJsonAsync($"{Agents}/{id}/owners",
            new { ownerUserId = other["sara"].Id }), HttpStatusCode.BadRequest));

        // People who are not designers of the unit do not find the agent, nor does another tenant.
        Assert.Equal("not_found", await ProblemAsync(t["emp"].Client.PutAsJsonAsync($"{Agents}/{id}/owners", new { backupOwnerUserId = t["bo"].Id }),
            HttpStatusCode.NotFound));
        Assert.Equal("not_found", await ProblemAsync(other["sara"].Client.PutAsJsonAsync($"{Agents}/{id}/owners", new { backupOwnerUserId = other["bo"].Id }),
            HttpStatusCode.NotFound));

        // A purpose over 1,000 characters is refused.
        var detail = await OkAsync(t["sara"].Client.GetAsync($"{Agents}/{id}"));
        var definition = JsonSerializer.Deserialize<Dictionary<string, object?>>(detail.GetProperty("definition").GetRawText())!;
        definition["purpose"] = new Dictionary<string, string> { ["en"] = new string('x', 1001), ["ar"] = "غرض" };
        Assert.Equal("validation.agent.purpose", await ProblemAsync(t["sara"].Client.PutAsJsonAsync($"{Agents}/{id}/draft",
            new { revision = detail.GetProperty("latest").GetProperty("revision").GetInt32(), definition }), HttpStatusCode.BadRequest));
    }

    [Fact]
    public async Task When_the_owner_leaves_the_backup_owner_takes_over_and_the_agent_keeps_running()
    {
        var t = await FreshAsync();
        var id = await PublishedAsync(t);
        await OkAsync(t["sara"].Client.PutAsJsonAsync($"{Agents}/{id}/owners", new { ownerUserId = t["sara"].Id, backupOwnerUserId = t["bo"].Id }));

        await LeavesAsync(t, "sara");
        var agent = (await OkAsync(t["bo"].Client.GetAsync($"{Agents}/{id}"))).GetProperty("agent");
        Assert.Equal(t["bo"].Id, agent.GetProperty("ownerUserId").GetGuid());
        Assert.Equal(JsonValueKind.Null, agent.GetProperty("backupOwnerUserId").ValueKind);
        Assert.NotEqual(JsonValueKind.Null, agent.GetProperty("ownerFlaggedAt").ValueKind);
        Assert.Equal("published", agent.GetProperty("status").GetString());
        Assert.Contains($"/admin/agents/{id}", await NotificationLinksAsync(t["bo"].Client));
        Assert.Equal(2, await OwnerAuditsAsync(t));

        // Naming owners again clears the mark; the agent still answers its audience.
        var cleared = await OkAsync(t["bo"].Client.PutAsJsonAsync($"{Agents}/{id}/owners", new { ownerUserId = t["bo"].Id }));
        Assert.Equal(JsonValueKind.Null, cleared.GetProperty("agent").GetProperty("ownerFlaggedAt").ValueKind);
        await OkAsync(t["emp"].Client.PostAsJsonAsync($"{Agents}/{id}/runs", new { message = "Hello" }));
    }

    [Fact]
    public async Task Without_a_backup_owner_an_ai_policy_administrator_takes_over_and_a_leaving_backup_is_removed()
    {
        var t = await FreshAsync();
        var alone = await PublishedAsync(t);
        var backed = await PublishedAsync(t);
        await OkAsync(t["sara"].Client.PutAsJsonAsync($"{Agents}/{backed}/owners", new { ownerUserId = t["sara"].Id, backupOwnerUserId = t["bo"].Id }));

        // The backup owner leaving only removes them.
        await LeavesAsync(t, "bo");
        var still = (await OkAsync(t["sara"].Client.GetAsync($"{Agents}/{backed}"))).GetProperty("agent");
        Assert.Equal(t["sara"].Id, still.GetProperty("ownerUserId").GetGuid());
        Assert.Equal(JsonValueKind.Null, still.GetProperty("backupOwnerUserId").ValueKind);

        // The tenant administrator and the AI Policy Administrator both hold ai.policy_manage; one of them takes both agents.
        await LeavesAsync(t, "sara");
        var owners = new HashSet<Guid>();
        foreach (var id in new[] { alone, backed })
        {
            var agent = (await OkAsync(t["aip"].Client.GetAsync($"{Agents}/{id}"))).GetProperty("agent");
            owners.Add(agent.GetProperty("ownerUserId").GetGuid());
            Assert.NotEqual(JsonValueKind.Null, agent.GetProperty("ownerFlaggedAt").ValueKind);
        }
        var successor = Assert.Single(owners);
        var name = successor == t["aip"].Id ? "aip" : "admin";
        Assert.Contains(successor, new[] { t["aip"].Id, t["admin"].Id });
        Assert.Contains($"/admin/agents/{alone}", await NotificationLinksAsync(t[name].Client));
    }
}
