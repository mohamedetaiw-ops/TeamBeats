using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using TeamBeats.Audit.Contracts;
using TeamBeats.Authorization.Contracts;
using TeamBeats.Modules.Agents;
using TeamBeats.Modules.Hr.Leave;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Testing;
using Xunit;

namespace TeamBeats.Integration.Tests;

/// <summary>
/// Spec 020 R2 slice 1 (T016, US4, FR-019): Agent Studio creates blank agents within the package and the custom agent
/// limit, clones copy audience and tools only when asked, and the tool picker shows what the package includes. Nothing
/// crosses org units or tenants. All content is fictional.
/// </summary>
[Collection(WorldCollection.Name)]
public sealed class AgentStudioTests(TenantWorld world)
{
    private const string Agents = "/api/v1/agents";

    private async Task<TestTenant> FreshAsync(string package = "workplace_plus")
    {
        var t = await world.CreateTenantAsync("as-" + Guid.NewGuid().ToString("N")[..8], package, "en", ["en", "ar"]);
        foreach (var name in new[] { "sara", "fadi", "emp", "aip" })
        {
            await world.SeedUserAsync(t, name, MembershipTypes.Employee);
        }
        var hr = await world.CreateUnitAsync(t, "Hr", null, "subsidiary");
        var finance = await world.CreateUnitAsync(t, "Finance", null, "subsidiary");
        await world.AssignAsync(t, "sara", BuiltInRoles.AgentDesigner, "org_subtree", hr);
        await world.AssignAsync(t, "fadi", BuiltInRoles.AgentDesigner, "org_subtree", finance);
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

    private static object Blank(Guid unit, string name = "Supplier onboarding helper") => new
    {
        templateId = (string?)null,
        orgUnitId = unit,
        name = new Dictionary<string, string> { ["en"] = name, ["ar"] = "مساعد تسجيل الموردين" },
        instructions = new Dictionary<string, string> { ["en"] = "Help colleagues register a new supplier.", ["ar"] = "ساعد الزملاء في تسجيل مورد جديد." },
        category = "requests",
        languages = new[] { "en", "ar" },
    };

    private Task<long> AuditCountAsync(TestTenant t, string action) =>
        world.Database.TenantScalarAsync<long>(t.Id, "select count(*) from audit.audit_event where action = @action", ("action", action));

    /// <summary>Holds the tenant on a Workplace Plus version whose custom agent limit is <paramref name="limit"/>, then republishes the package as it was.</summary>
    private async Task CapCustomAgentsAsync(TestTenant t, int limit)
    {
        var current = (await OkAsync(t["admin"].Client.GetAsync("/api/v1/tenant/entitlements"))).GetProperty("capabilities");
        var capped = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(current.GetRawText())!.ToDictionary(p => p.Key, p => (object)p.Value);
        capped[TeamBeats.Entitlements.Contracts.Capabilities.AiAgentsMaxCustom] = new { enabled = true, limit };
        var draft = await OkAsync(world.Platform.PostAsJsonAsync("/api/v1/platform/packages/workplace_plus/versions", new { capabilities = capped }), HttpStatusCode.Created);
        var version = draft.GetProperty("version").GetInt32();
        (await world.Platform.PostAsync($"/api/v1/platform/packages/workplace_plus/versions/{version}:publish", null)).EnsureSuccessStatusCode();
        (await world.Platform.PutAsJsonAsync($"/api/v1/platform/tenants/{t.Id}/subscription", new { packageKey = "workplace_plus", version })).EnsureSuccessStatusCode();
        var restore = await OkAsync(world.Platform.PostAsJsonAsync("/api/v1/platform/packages/workplace_plus/versions", new { capabilities = current }), HttpStatusCode.Created);
        (await world.Platform.PostAsync($"/api/v1/platform/packages/workplace_plus/versions/{restore.GetProperty("version").GetInt32()}:publish", null)).EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task A_blank_agent_starts_safe_in_the_designers_unit_and_stays_within_the_custom_limit()
    {
        var t = await FreshAsync();
        await CapCustomAgentsAsync(t, 1);
        var sara = t["sara"].Client;
        var offer = await OkAsync(sara.GetAsync($"{Agents}/studio"));
        Assert.True(offer.GetProperty("custom").GetBoolean());
        Assert.Equal(0, offer.GetProperty("customCount").GetInt64());
        Assert.Equal(1, offer.GetProperty("customLimit").GetInt64());
        Assert.False(offer.GetProperty("jsonView").GetBoolean());
        Assert.True((await OkAsync(t["aip"].Client.GetAsync($"{Agents}/studio"))).GetProperty("jsonView").GetBoolean());

        var created = await OkAsync(sara.PostAsJsonAsync(Agents, Blank(t.Unit("Hr"))), HttpStatusCode.Created);
        var agent = created.GetProperty("agent");
        Assert.Equal(JsonValueKind.Null, agent.GetProperty("templateId").ValueKind);
        Assert.Equal(AgentStatuses.Draft, agent.GetProperty("status").GetString());
        var definition = created.GetProperty("definition");
        Assert.Equal("L0", definition.GetProperty("autonomy").GetString());
        Assert.Empty(definition.GetProperty("tools").EnumerateArray());
        Assert.Equal(t.Unit("Hr"), Assert.Single(definition.GetProperty("audience").GetProperty("orgUnits").EnumerateArray()).GetGuid());
        Assert.Equal(1, await AuditCountAsync(t, AuditActions.AgentCreated));

        // The limit is reached: the designer is told so, with who can raise it.
        Assert.Equal("conflict.agent.custom_limit", await ProblemAsync(sara.PostAsJsonAsync(Agents, Blank(t.Unit("Hr"), "Second helper")), HttpStatusCode.Conflict));
        Assert.Equal(1, (await OkAsync(sara.GetAsync($"{Agents}/studio"))).GetProperty("customCount").GetInt64());

        // The first step's fields are checked like any draft; another unit's designer and employees cannot create here.
        var other = await FreshAsync();
        Assert.Equal("validation.agent.name", await ProblemAsync(other["sara"].Client.PostAsJsonAsync(Agents, new
        {
            templateId = (string?)null, orgUnitId = other.Unit("Hr"), instructions = new Dictionary<string, string> { ["en"] = "x" }, category = "other", languages = new[] { "en" },
        }), HttpStatusCode.BadRequest));
        Assert.Equal(HttpStatusCode.Forbidden, (await other["fadi"].Client.PostAsJsonAsync(Agents, Blank(other.Unit("Hr")))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await other["emp"].Client.GetAsync($"{Agents}/studio")).StatusCode);

        // Workplace adapts templates only (open question 2).
        var plain = await FreshAsync("workplace");
        Assert.False((await OkAsync(plain["sara"].Client.GetAsync($"{Agents}/studio"))).GetProperty("custom").GetBoolean());
        Assert.Equal(HttpStatusCode.Forbidden, (await plain["sara"].Client.PostAsJsonAsync(Agents, Blank(plain.Unit("Hr")))).StatusCode);
    }

    [Fact]
    public async Task A_clone_is_a_new_draft_and_copies_audience_and_tools_only_when_asked()
    {
        var t = await FreshAsync();
        var sara = t["sara"].Client;
        var source = await OkAsync(sara.PostAsJsonAsync(Agents, new { templateId = "enterprise_knowledge", orgUnitId = t.Unit("Hr") }), HttpStatusCode.Created);
        var sourceId = source.GetProperty("agent").GetProperty("id").GetGuid();
        var sourceTools = source.GetProperty("definition").GetProperty("tools").EnumerateArray().Select(x => x.GetString()).ToList();
        Assert.NotEmpty(sourceTools);
        var name = new Dictionary<string, string> { ["en"] = "Knowledge for HR", ["ar"] = "المعرفة للموارد البشرية" };

        var bare = await OkAsync(sara.PostAsJsonAsync($"{Agents}/{sourceId}/clone", new { orgUnitId = t.Unit("Hr"), name }), HttpStatusCode.Created);
        Assert.NotEqual(sourceId, bare.GetProperty("agent").GetProperty("id").GetGuid());
        Assert.Empty(bare.GetProperty("definition").GetProperty("tools").EnumerateArray());
        Assert.Equal(1, bare.GetProperty("latest").GetProperty("number").GetInt32());
        Assert.Equal(JsonValueKind.Null, bare.GetProperty("agent").GetProperty("currentVersion").ValueKind);
        // A clone of a template agent keeps the template, so it is not a custom agent.
        Assert.Equal("enterprise_knowledge", bare.GetProperty("agent").GetProperty("templateId").GetString());
        Assert.Equal("Knowledge for HR", bare.GetProperty("definition").GetProperty("name").GetProperty("en").GetString());

        var full = await OkAsync(sara.PostAsJsonAsync($"{Agents}/{sourceId}/clone", new { orgUnitId = t.Unit("Hr"), name, copyTools = true, copyAudience = true }),
            HttpStatusCode.Created);
        Assert.Equal(sourceTools, full.GetProperty("definition").GetProperty("tools").EnumerateArray().Select(x => x.GetString()).ToList());
        Assert.Equal(2, await world.Database.TenantScalarAsync<long>(t.Id, "select count(*) from audit.audit_event where action = @action", ("action", AuditActions.AgentCloned)));

        // Only a designer of the target unit who can open the source clones it; another tenant does not find it.
        Assert.Equal(HttpStatusCode.Forbidden, (await sara.PostAsJsonAsync($"{Agents}/{sourceId}/clone", new { orgUnitId = t.Unit("Finance"), name })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await t["fadi"].Client.PostAsJsonAsync($"{Agents}/{sourceId}/clone", new { orgUnitId = t.Unit("Finance"), name })).StatusCode);
        var other = await FreshAsync();
        Assert.Equal(HttpStatusCode.NotFound, (await other["sara"].Client.PostAsJsonAsync($"{Agents}/{sourceId}/clone", new { orgUnitId = other.Unit("Hr"), name })).StatusCode);
    }

    [Fact]
    public async Task The_tool_picker_says_which_tools_the_package_includes()
    {
        var t = await FreshAsync("workplace");
        var tools = (await OkAsync(t["sara"].Client.GetAsync($"{Agents}/tools"))).GetProperty("tools").EnumerateArray().ToList();
        var entitlements = (await OkAsync(t["admin"].Client.GetAsync("/api/v1/tenant/entitlements"))).GetProperty("capabilities");
        var leave = entitlements.TryGetProperty(TeamBeats.Entitlements.Contracts.Capabilities.HrLeave, out var c) && c.GetProperty("enabled").GetBoolean();
        var balance = tools.Single(x => x.GetProperty("name").GetString() == LeaveBalanceTool.Name);
        Assert.Equal(leave, balance.GetProperty("inPackage").GetBoolean());
        Assert.All(tools.Where(x => !x.GetProperty("inPackage").GetBoolean()), x => Assert.False(x.GetProperty("allowed").GetBoolean()));
    }
}
