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
/// Spec 025 T016 (US8 scenario 3, FR-015): compliance officers map a policy to controls of the spec 026 control library,
/// platform sets and the tenant's own regulation frameworks; an unknown control is refused; a control lists its policies
/// with their status as far as the reader may see them; packages without <c>policy.evidence</c> and other tenants reach
/// nothing.
/// </summary>
[Collection(WorldCollection.Name)]
public sealed class PolicyControlTests(TenantWorld world)
{
    private const string Policies = "/api/v1/policies";

    private async Task<TestTenant> FreshAsync(string package = "enterprise_gov")
    {
        var t = await world.CreateTenantAsync("pct-" + Guid.NewGuid().ToString("N")[..8], package, "en", ["en", "ar"]);
        var holding = await world.CreateUnitAsync(t, "Holding", null, "organization");
        await world.CreateUnitAsync(t, "Gulf", holding, "subsidiary");
        await world.CreateUnitAsync(t, "Egypt", holding, "subsidiary");
        foreach (var (name, unit) in new[] { ("co", "Gulf"), ("legal", "Gulf"), ("ali", "Gulf"), ("eman", "Egypt") })
        {
            await world.SeedUserAsync(t, name, MembershipTypes.Employee);
            await world.AddMembershipAsync(t, name, unit);
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

    private static async Task<string[]> ErrorKeysAsync(HttpResponseMessage response)
    {
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        if (body.TryGetProperty("errors", out var errors))
        {
            return errors.EnumerateObject().SelectMany(e => e.Value.EnumerateArray().Select(v => v.GetProperty("key").GetString()!)).ToArray();
        }
        return body.TryGetProperty("detailKey", out var detail) ? [detail.GetString()!] : [body.GetProperty("code").GetString()!];
    }

    private static Dictionary<string, string> Both(string text) => new() { ["en"] = text, ["ar"] = text + " ع" };

    /// <summary>A policy in force, for everyone or for one unit's people only.</summary>
    private static async Task<Guid> PublishedAsync(TestTenant t, string code, Guid? unit = null)
    {
        var id = (await OkAsync(t["co"].Client.PostAsJsonAsync(Policies, new
        {
            code, names = Both(code), orgUnitId = unit, route = new[] { new { approvers = new[] { t["legal"].Id } } },
        }), HttpStatusCode.Created)).GetProperty("id").GetGuid();
        var clause = new
        {
            level = 1, titles = Both("Use"),
            bodies = new Dictionary<string, object>
            {
                ["en"] = new { blocks = new object[] { new { type = "paragraph", children = new[] { new { text = "Use it well." } } } } },
                ["ar"] = new { blocks = new object[] { new { type = "paragraph", children = new[] { new { text = "استخدمه جيدًا." } } } } },
            },
        };
        await OkAsync(t["co"].Client.PutAsJsonAsync($"{Policies}/{id}/draft", new { titles = Both(code), clauses = new[] { clause } }));
        await OkAsync(t["co"].Client.PostAsJsonAsync($"{Policies}/{id}/submit", new { }));
        var task = (await OkAsync(t["legal"].Client.GetAsync("/api/v1/tasks"))).EnumerateArray().Single(i => i.GetProperty("source").GetString() == "policy_approval");
        await OkAsync(t["legal"].Client.PostAsJsonAsync($"/api/v1/tasks/{task.GetProperty("taskId").GetGuid()}/decision", new { outcome = "approve" }));
        return id;
    }

    [Fact]
    public async Task Officers_map_policies_to_library_controls_and_a_control_lists_its_policies_with_their_status()
    {
        var t = await FreshAsync();
        var aup = await PublishedAsync(t, "AUP");
        var units = (await OkAsync(t["admin"].Client.GetAsync("/api/v1/org/units"))).EnumerateArray().ToList();
        var egypt = units.Single(u => u.GetProperty("names").GetProperty("en").GetString() == "Egypt").GetProperty("id").GetGuid();
        var egyptOnly = await PublishedAsync(t, "EG-AUP", egypt);
        Assert.True((await OkAsync(t["co"].Client.GetAsync($"{Policies}/{aup}"))).GetProperty("rights").GetProperty("controls").GetBoolean());

        // The library offers the platform sets and the tenant's own regulation framework.
        await OkAsync(t["co"].Client.PostAsJsonAsync("/api/v1/compliance/library", new
        {
            key = "pdpl", version = "2021", names = new { en = "Personal data protection law" },
            controls = new object[] { new { code = "Art-5", titles = new { en = "Lawful processing" } } },
        }), HttpStatusCode.Created);
        var library = (await OkAsync(t["co"].Client.GetAsync($"{Policies}/control-library"))).EnumerateArray().Select(f => f.GetProperty("key").GetString()).ToList();
        Assert.Contains("iso27001", library);
        Assert.Contains("tenant.pdpl", library);
        Assert.Equal(HttpStatusCode.Forbidden, (await t["ali"].Client.GetAsync($"{Policies}/control-library")).StatusCode);

        // Only known controls, and only compliance officers map them.
        var path = $"{Policies}/{aup}/controls";
        Assert.Contains("validation.policies.control_unknown", await ErrorKeysAsync(await t["co"].Client.PutAsJsonAsync(path,
            new { controls = new[] { new { framework = "iso27001", code = "9.99" } } })));
        Assert.Contains("validation.policies.control_unknown", await ErrorKeysAsync(await t["co"].Client.PutAsJsonAsync(path,
            new { controls = new[] { new { framework = "nope", code = "5.10" } } })));
        Assert.Equal(HttpStatusCode.Forbidden, (await t["ali"].Client.PutAsJsonAsync(path, new { controls = new[] { new { framework = "iso27001", code = "5.10" } } })).StatusCode);
        var saved = await OkAsync(t["co"].Client.PutAsJsonAsync(path, new
        {
            controls = new[] { new { framework = "iso27001", code = "5.10" }, new { framework = "tenant.pdpl", code = "art-5" }, new { framework = "iso27001", code = "5.10" } },
        }));
        var mapped = saved.GetProperty("controls").EnumerateArray().ToList();
        Assert.Equal(2, mapped.Count);
        Assert.Contains(mapped, c => c.GetProperty("code").GetString() == "Art-5" && c.GetProperty("framework").GetString() == "tenant.pdpl");
        Assert.All(mapped, c => Assert.True(c.GetProperty("known").GetBoolean()));
        await world.Database.AssertAuditedAsync(t.Id, AuditActions.PolicyControlsChanged, aup.ToString());
        await OkAsync(t["co"].Client.PutAsJsonAsync($"{Policies}/{egyptOnly}/controls", new { controls = new[] { new { framework = "iso27001", code = "5.10" } } }));

        // Readers of the policy see its controls; the control lists its policies as far as each reader sees them.
        var readerView = await OkAsync(t["ali"].Client.GetAsync(path));
        Assert.Equal(2, readerView.GetProperty("controls").GetArrayLength());
        Assert.False(readerView.GetProperty("canEdit").GetBoolean());
        var forOfficer = await OkAsync(t["co"].Client.GetAsync($"{Policies}/by-control?framework=iso27001&code=5.10"));
        Assert.Equal("5.10", forOfficer.GetProperty("control").GetProperty("code").GetString());
        Assert.Equal(["AUP", "EG-AUP"], forOfficer.GetProperty("policies").EnumerateArray().Select(p => p.GetProperty("policy").GetProperty("code").GetString()!).ToArray());
        Assert.All(forOfficer.GetProperty("policies").EnumerateArray(), p => Assert.Equal(1, p.GetProperty("currentVersion").GetInt32()));
        var forAli = await OkAsync(t["ali"].Client.GetAsync($"{Policies}/by-control?framework=iso27001&code=5.10"));
        Assert.Equal(["AUP"], forAli.GetProperty("policies").EnumerateArray().Select(p => p.GetProperty("policy").GetProperty("code").GetString()!).ToArray());
        Assert.Equal(HttpStatusCode.NotFound, (await t["co"].Client.GetAsync($"{Policies}/by-control?framework=iso27001&code=9.99")).StatusCode);

        // Removing a mapping takes the policy off the control.
        await OkAsync(t["co"].Client.PutAsJsonAsync(path, new { controls = new[] { new { framework = "tenant.pdpl", code = "Art-5" } } }));
        Assert.Equal(["EG-AUP"], (await OkAsync(t["co"].Client.GetAsync($"{Policies}/by-control?framework=iso27001&code=5.10"))).GetProperty("policies").EnumerateArray()
            .Select(p => p.GetProperty("policy").GetProperty("code").GetString()!).ToArray());
    }

    [Fact]
    public async Task Without_policy_evidence_there_is_no_mapping_and_other_tenants_see_nothing()
    {
        var plain = await FreshAsync("workplace_plus");
        var policy = await PublishedAsync(plain, "AUP");
        Assert.False((await OkAsync(plain["co"].Client.GetAsync($"{Policies}/{policy}"))).GetProperty("rights").GetProperty("controls").GetBoolean());
        Assert.Equal(HttpStatusCode.Forbidden, (await plain["co"].Client.GetAsync($"{Policies}/{policy}/controls")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await plain["co"].Client.PutAsJsonAsync($"{Policies}/{policy}/controls",
            new { controls = new[] { new { framework = "iso27001", code = "5.10" } } })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await plain["co"].Client.GetAsync($"{Policies}/by-control?framework=iso27001&code=5.10")).StatusCode);

        var t = await FreshAsync();
        var own = await PublishedAsync(t, "AUP");
        await OkAsync(t["co"].Client.PutAsJsonAsync($"{Policies}/{own}/controls", new { controls = new[] { new { framework = "iso27001", code = "5.10" } } }));
        var other = await FreshAsync();
        Assert.Equal(HttpStatusCode.NotFound, (await other["co"].Client.GetAsync($"{Policies}/{own}/controls")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other["co"].Client.PutAsJsonAsync($"{Policies}/{own}/controls", new { controls = Array.Empty<object>() })).StatusCode);
        Assert.Equal(0, (await OkAsync(other["co"].Client.GetAsync($"{Policies}/by-control?framework=iso27001&code=5.10"))).GetProperty("policies").GetArrayLength());
    }
}
