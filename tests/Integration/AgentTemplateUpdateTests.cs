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
/// Template updates (spec 020 FR-005 and the edge case "a template update arrives while a draft is in review"): a newer
/// platform version of an agent's template is offered and never applied on its own; applying it starts a draft with the
/// template's behaviour and the tenant's own name, audience and purpose; a version in review is left alone. Nothing
/// crosses tenants.
/// </summary>
[Collection(WorldCollection.Name)]
public sealed class AgentTemplateUpdateTests(TenantWorld world)
{
    private const string Agents = "/api/v1/agents";

    private async Task<TestTenant> FreshAsync()
    {
        var t = await world.CreateTenantAsync("at-" + Guid.NewGuid().ToString("N")[..8], "workplace_plus", "en", ["en", "ar"]);
        foreach (var name in new[] { "sara", "emp" })
        {
            await world.SeedUserAsync(t, name, MembershipTypes.Employee);
        }
        var hr = await world.CreateUnitAsync(t, "Hr", null, "subsidiary");
        await world.AddMembershipAsync(t, "emp", "Hr");
        await world.AssignAsync(t, "sara", BuiltInRoles.AgentDesigner, "org_subtree", hr);
        return t;
    }

    private static async Task<JsonElement> OkAsync(Task<HttpResponseMessage> call, HttpStatusCode expected = HttpStatusCode.OK)
    {
        var response = await call;
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == expected, $"{(int)response.StatusCode}: {text}");
        return text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone();
    }

    private static async Task<string> CodeAsync(Task<HttpResponseMessage> call, HttpStatusCode expected)
    {
        var body = await OkAsync(call, expected);
        return body.TryGetProperty("detailKey", out var detail) && detail.ValueKind == JsonValueKind.String ? detail.GetString()! : body.GetProperty("code").GetString()!;
    }

    /// <summary>Installs the IT Service Desk template (version 2) and makes the agent look as if it came from version 1, with the tenant's own edits.</summary>
    private async Task<(Guid Id, JsonElement Template)> OlderAgentAsync(TestTenant t)
    {
        var detail = await OkAsync(t["sara"].Client.PostAsJsonAsync(Agents, new { templateId = "it_service_desk", orgUnitId = t.Unit("Hr") }), HttpStatusCode.Created);
        var id = detail.GetProperty("agent").GetProperty("id").GetGuid();
        var template = detail.GetProperty("definition").Clone();
        var definition = JsonSerializer.Deserialize<Dictionary<string, object?>>(template.GetRawText())!;
        definition["name"] = new Dictionary<string, string> { ["en"] = "Our IT desk", ["ar"] = "مكتب التقنية لدينا" };
        definition["instructions"] = new Dictionary<string, string> { ["en"] = "Old instructions.", ["ar"] = "تعليمات قديمة." };
        definition["tools"] = new[] { "knowledge.search" };
        definition["purpose"] = new Dictionary<string, string> { ["en"] = "Fix laptops." };
        await OkAsync(t["sara"].Client.PutAsJsonAsync($"{Agents}/{id}/draft", new { revision = 1, definition }));
        await world.Database.TenantScalarAsync<int>(t.Id, "update agents.agent set template_version = 1 where id = @id returning 1", ("id", id));
        return (id, template);
    }

    [Fact]
    public async Task A_newer_template_is_offered_and_applying_it_starts_a_draft_that_keeps_the_tenants_choices()
    {
        var t = await FreshAsync();
        var (id, template) = await OlderAgentAsync(t);
        var offered = await OkAsync(t["sara"].Client.GetAsync($"{Agents}/{id}"));
        Assert.Equal(2, offered.GetProperty("templateUpdate").GetInt32());
        Assert.Equal("Old instructions.", offered.GetProperty("definition").GetProperty("instructions").GetProperty("en").GetString());

        // Only the offered version, and only by designers of the unit; another tenant does not find the agent.
        Assert.Equal("conflict.agent.no_template_update", await CodeAsync(t["sara"].Client.PostAsJsonAsync($"{Agents}/{id}/template-update", new { version = 1 }),
            HttpStatusCode.Conflict));
        await OkAsync(t["emp"].Client.PostAsJsonAsync($"{Agents}/{id}/template-update", new { version = 2 }), HttpStatusCode.NotFound);
        var other = await FreshAsync();
        await OkAsync(other["sara"].Client.PostAsJsonAsync($"{Agents}/{id}/template-update", new { version = 2 }), HttpStatusCode.NotFound);

        var applied = await OkAsync(t["sara"].Client.PostAsJsonAsync($"{Agents}/{id}/template-update", new { version = 2 }));
        var definition = applied.GetProperty("definition");
        Assert.Equal(template.GetProperty("instructions").GetProperty("en").GetString(), definition.GetProperty("instructions").GetProperty("en").GetString());
        Assert.Equal(template.GetProperty("tools").GetRawText(), definition.GetProperty("tools").GetRawText());
        Assert.Equal("Our IT desk", definition.GetProperty("name").GetProperty("en").GetString());
        Assert.Equal("Fix laptops.", definition.GetProperty("purpose").GetProperty("en").GetString());
        Assert.Equal(t.Unit("Hr"), definition.GetProperty("audience").GetProperty("orgUnits")[0].GetGuid());
        Assert.Equal("draft", applied.GetProperty("latest").GetProperty("state").GetString());
        Assert.Equal(2, applied.GetProperty("agent").GetProperty("templateVersion").GetInt32());
        Assert.Equal(JsonValueKind.Null, applied.GetProperty("templateUpdate").ValueKind);
        Assert.Equal(1, await world.Database.CountInTenantAsync(t.Id, "select count(*) from audit.audit_event where action = @a",
            ("a", AuditActions.AgentTemplateUpdateApplied)));
    }

    [Fact]
    public async Task A_version_in_review_is_left_alone_and_the_update_stays_offered()
    {
        var t = await FreshAsync();
        var (id, _) = await OlderAgentAsync(t);
        await OkAsync(t["sara"].Client.PostAsJsonAsync($"{Agents}/{id}/submit", new { revision = 2 }));
        Assert.Equal("conflict.agent.in_review", await CodeAsync(t["sara"].Client.PostAsJsonAsync($"{Agents}/{id}/template-update", new { version = 2 }),
            HttpStatusCode.Conflict));
        var detail = await OkAsync(t["sara"].Client.GetAsync($"{Agents}/{id}"));
        Assert.Equal("Old instructions.", detail.GetProperty("definition").GetProperty("instructions").GetProperty("en").GetString());
        Assert.Equal(2, detail.GetProperty("templateUpdate").GetInt32());
    }
}
