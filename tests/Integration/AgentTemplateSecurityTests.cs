using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TeamBeats.Audit.Contracts;
using TeamBeats.Authorization.Contracts;
using TeamBeats.Modules.Agents;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Testing;
using Xunit;

namespace TeamBeats.Integration.Tests;

/// <summary>
/// Forced security updates of platform templates (spec 020 FR-005, edge case "a platform security update to a template"):
/// an update in force applies to every agent built on the template without review, a draft cannot undo it, its owners
/// are told when it is announced and when it applies (once each), the application is audited, and an agent installed
/// afterwards starts with it. Nothing crosses tenants.
/// </summary>
[Collection(WorldCollection.Name)]
public sealed class AgentTemplateSecurityTests(TenantWorld world)
{
    private const string Agents = "/api/v1/agents";
    private static readonly Dictionary<string, string> Reason = new() { ["en"] = "Drafting tool misused.", ["ar"] = "أسيء استخدام أداة الصياغة." };

    private async Task<TestTenant> FreshAsync()
    {
        var t = await world.CreateTenantAsync("as-" + Guid.NewGuid().ToString("N")[..8], "workplace_plus", "en", ["en", "ar"]);
        await world.SeedUserAsync(t, "sara", MembershipTypes.Employee);
        var hr = await world.CreateUnitAsync(t, "Hr", null, "subsidiary");
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

    private Task<JsonElement> InstallAsync(TestTenant t) =>
        OkAsync(t["sara"].Client.PostAsJsonAsync(Agents, new { templateId = "it_service_desk", orgUnitId = t.Unit("Hr") }), HttpStatusCode.Created);

    private Task<int> SweepAsync(TestTenant t) =>
        world.Factory.InScopeAsync(t.Id, async sp =>
        {
            var changed = await sp.GetRequiredService<AgentTemplateSecurity>().ApplyAsync(CancellationToken.None);
            await sp.GetRequiredService<IDbSession>().CommitAsync(CancellationToken.None);
            return changed;
        });

    private Task<long> NotifiedAsync(TestTenant t, string template) =>
        world.Database.CountInTenantAsync(t.Id, "select count(*) from notifications.notification where user_id = @user and template_key = @template",
            ("user", t["sara"].Id), ("template", template));

    private Task<long> AuditedAsync(TestTenant t) =>
        world.Database.CountInTenantAsync(t.Id, "select count(*) from audit.audit_event where action = @a", ("a", AuditActions.AgentTemplateSecurityApplied));

    [Fact]
    public async Task An_update_in_force_applies_without_review_cannot_be_undone_in_a_draft_and_is_announced_and_audited_once()
    {
        var t = await FreshAsync();
        var other = await FreshAsync();
        var detail = await InstallAsync(t);
        var id = detail.GetProperty("agent").GetProperty("id").GetGuid();
        var before = detail.GetProperty("definition").Clone();
        Assert.Contains("requests.prepare", before.GetProperty("tools").EnumerateArray().Select(x => x.GetString()));
        await InstallAsync(other);

        var now = DateTimeOffset.UtcNow;
        world.Factory.TemplateSecurity.Set(
            new TemplateSecurityUpdate("it_service_desk", 1, Reason, now.AddHours(-1), Critical: true, RemoveTools: ["requests.prepare"], MaxAutonomy: "L1"),
            new TemplateSecurityUpdate("it_service_desk", 2, Reason, now.AddHours(-1), now.AddDays(7), MaxToolCalls: 4));
        try
        {
            // The editor shows both: the critical one in force, the dated one announced.
            var shown = await OkAsync(t["sara"].Client.GetAsync($"{Agents}/{id}"));
            var updates = shown.GetProperty("securityUpdates").EnumerateArray().ToList();
            Assert.Equal(2, updates.Count);
            Assert.True(updates[0].GetProperty("inForce").GetBoolean());
            Assert.False(updates[1].GetProperty("inForce").GetBoolean());

            // A draft that still holds what the update takes away is refused; one without it saves.
            var definition = JsonSerializer.Deserialize<Dictionary<string, object?>>(before.GetRawText())!;
            var refused = await OkAsync(t["sara"].Client.PutAsJsonAsync($"{Agents}/{id}/draft", new { revision = 1, definition }), HttpStatusCode.BadRequest);
            Assert.Contains("validation.agent.template_security", refused.GetRawText());
            definition["tools"] = before.GetProperty("tools").EnumerateArray().Select(x => x.GetString()).Where(x => x != "requests.prepare").ToArray();
            definition["autonomy"] = "L1";
            await OkAsync(t["sara"].Client.PutAsJsonAsync($"{Agents}/{id}/draft", new { revision = 1, definition }));

            // The sweep tells the owner once that one applied and once that one is coming, and audits the applied one.
            Assert.Equal(1, await SweepAsync(t));
            Assert.Equal(0, await SweepAsync(t));
            Assert.Equal(1L, await NotifiedAsync(t, AgentTemplateSecurity.AppliedTemplate));
            Assert.Equal(1L, await NotifiedAsync(t, AgentTemplateSecurity.AnnouncedTemplate));
            Assert.Equal(1L, await AuditedAsync(t));

            // An agent installed now starts with the update, so its owner is told only of the one still coming.
            var later = await InstallAsync(t);
            Assert.DoesNotContain("requests.prepare", later.GetProperty("definition").GetProperty("tools").EnumerateArray().Select(x => x.GetString()));
            Assert.Equal("L1", later.GetProperty("definition").GetProperty("autonomy").GetString());
            Assert.Equal(1, await SweepAsync(t));
            Assert.Equal(1L, await AuditedAsync(t));

            // The other tenant's agent is reached only by its own tenant's sweep.
            Assert.Equal(0L, await AuditedAsync(other));
            Assert.Equal(1, await SweepAsync(other));
            Assert.Equal(1L, await AuditedAsync(other));
        }
        finally
        {
            world.Factory.TemplateSecurity.Clear();
        }
    }
}
