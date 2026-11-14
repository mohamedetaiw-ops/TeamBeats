using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TeamBeats.Audit.Contracts;
using TeamBeats.Authorization.Contracts;
using TeamBeats.Modules.AiGateway.Contracts;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Testing;
using Xunit;

namespace TeamBeats.Integration.Tests;

/// <summary>
/// Spec 013 T021 (US7, FR-009): org unit policies only narrow the policy above them. A unit restricted to one model
/// serves only that model for calls whose acting unit is the unit or a child; a save that loosens a parent rule is refused
/// with the rule and where it comes from; the acting unit is resolved on the server; several units mean the strictest
/// policy; a unit's AI Policy Administrator changes only their subtree; and policies never cross tenants.
/// </summary>
[Collection(WorldCollection.Name)]
public sealed class AiOrgPolicyTests(TenantWorld world)
{
    private const string Small = "openai/gpt-4o-mini";
    private const string Large = "openai/gpt-4o";
    private const string Embedding = "openai/text-embedding-3-small";

    private sealed record Org(TestTenant T, Guid Gulf, Guid GulfHr, Guid Africa);

    private async Task<Org> FreshAsync()
    {
        var t = await world.CreateTenantAsync("aio-" + Guid.NewGuid().ToString("N")[..8], "workplace_plus");
        await world.SeedUserAsync(t, "layla", MembershipTypes.Employee);
        await world.AssignAsync(t, "layla", BuiltInRoles.AiPolicyAdministrator, "tenant", null);
        await world.SeedUserAsync(t, "gulfadmin", MembershipTypes.Employee);
        await world.SeedUserAsync(t, "emp", MembershipTypes.Employee);
        var gulf = await world.CreateUnitAsync(t, "Gulf", null, "subsidiary");
        var hr = await world.CreateUnitAsync(t, "GulfHr", gulf, "department");
        var africa = await world.CreateUnitAsync(t, "Africa", null, "subsidiary");
        await world.AddMembershipAsync(t, "emp", "GulfHr");
        await world.AssignAsync(t, "gulfadmin", BuiltInRoles.AiPolicyAdministrator, "org_subtree", gulf);

        // Summaries prefer the larger model; the assistant and agents keep the platform route so the change applies at once.
        var routes = AiUseCases.All.Where(u => u is not (AiUseCases.Transcribe or AiUseCases.Speak)).ToDictionary(u => u, u => u == AiUseCases.Embed ? new[] { Embedding } : u == AiUseCases.Summarize ? new[] { Large, Small } : new[] { Small, Large });
        var current = await t["layla"].Client.GetAsync("/api/v1/ai/policy");
        using var put = new HttpRequestMessage(HttpMethod.Put, "/api/v1/ai/policy")
        {
            Content = JsonContent.Create(new { allowlist = new[] { Small, Large, Embedding }, routes, confidential = Array.Empty<string>() }),
        };
        put.Headers.TryAddWithoutValidation("If-Match", current.Headers.ETag?.ToString());
        await OkAsync(t["layla"].Client.SendAsync(put));
        return new Org(t, gulf, hr, africa);
    }

    private static async Task<JsonElement> OkAsync(Task<HttpResponseMessage> call, HttpStatusCode expected = HttpStatusCode.OK)
    {
        var response = await call;
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == expected, $"{(int)response.StatusCode}: {text}");
        return text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone();
    }

    private static Task<JsonElement> SaveAsync(TestTenant t, string user, Guid unit, object policy, HttpStatusCode expected = HttpStatusCode.OK) =>
        OkAsync(t[user].Client.PutAsJsonAsync($"/api/v1/ai/org-policies/{unit}", policy), expected);

    private Task<AiResult> ChatAsync(TestTenant t, string user, Guid? unit, string useCase = AiUseCases.Summarize, string? model = null,
        DataClassification classification = DataClassification.Internal, IReadOnlyList<Guid>? resources = null) =>
        world.Factory.AsUserAsync(t.Id, t[user].Id, sp => sp.GetRequiredService<IAiGateway>().ChatAsync(
            new AiRequest(useCase, [new AiMessage(AiRoles.User, "Summarize this")], classification, Model: model, ActingUnitId: unit, ResourceUnitIds: resources)));

    [Fact]
    public async Task A_unit_restricted_to_one_model_serves_only_that_model_for_itself_and_its_children()
    {
        var o = await FreshAsync();
        Assert.Equal(Large, (await ChatAsync(o.T, "layla", o.Gulf)).Completion!.Model);

        var saved = await SaveAsync(o.T, "gulfadmin", o.Gulf, new { allowlist = new[] { Small } });
        Assert.Equal(1, saved.GetProperty("version").GetInt32());
        await world.Database.AssertAuditedAsync(o.T.Id, AuditActions.AiOrgPolicyChanged);

        Assert.Equal(Small, (await ChatAsync(o.T, "layla", o.Gulf)).Completion!.Model);
        Assert.Equal(Small, (await ChatAsync(o.T, "layla", o.GulfHr)).Completion!.Model);
        Assert.Equal(AiDenials.ModelNotAllowed, (await ChatAsync(o.T, "layla", o.Gulf, model: Large)).Denial);
        Assert.Equal(Large, (await ChatAsync(o.T, "layla", o.Africa)).Completion!.Model);
        Assert.Equal(Large, (await ChatAsync(o.T, "layla", null)).Completion!.Model);

        // The acting unit comes from the server: emp's primary membership is in Gulf HR.
        Assert.Equal(Small, (await ChatAsync(o.T, "emp", null)).Completion!.Model);
        // A request about resources of several units takes the strictest policy.
        Assert.Equal(Small, (await ChatAsync(o.T, "layla", o.Africa, resources: [o.Gulf])).Completion!.Model);

        // Removing the restriction lets the unit follow the tenant policy again.
        await OkAsync(o.T["gulfadmin"].Client.DeleteAsync($"/api/v1/ai/org-policies/{o.Gulf}"), HttpStatusCode.NoContent);
        Assert.Equal(Large, (await ChatAsync(o.T, "layla", o.Gulf)).Completion!.Model);
    }

    [Fact]
    public async Task A_save_that_loosens_a_rule_above_is_refused_with_the_rule_named()
    {
        var o = await FreshAsync();
        await SaveAsync(o.T, "layla", o.Gulf, new { allowlist = new[] { Small }, maxClassification = "internal", maxOutputTokens = 1000 });

        var model = await SaveAsync(o.T, "layla", o.GulfHr, new { allowlist = new[] { Small, Large } }, HttpStatusCode.BadRequest);
        Assert.Equal("validation.ai.org_policy_loosens", model.GetProperty("errors").GetProperty("allowlist")[0].GetProperty("key").GetString());
        Assert.Equal(o.Gulf.ToString(), model.GetProperty("parentRules").GetProperty("allowlist").GetString());

        var level = await SaveAsync(o.T, "layla", o.GulfHr, new { maxClassification = "confidential" }, HttpStatusCode.BadRequest);
        Assert.Equal(o.Gulf.ToString(), level.GetProperty("parentRules").GetProperty("maxClassification").GetString());
        var tokens = await SaveAsync(o.T, "layla", o.GulfHr, new { maxOutputTokens = 4000 }, HttpStatusCode.BadRequest);
        Assert.Equal(o.Gulf.ToString(), tokens.GetProperty("parentRules").GetProperty("maxOutputTokens").GetString());

        // A model the tenant does not allow names the tenant policy.
        var tenant = await SaveAsync(o.T, "layla", o.Africa, new { allowlist = new[] { "unknown/model" } }, HttpStatusCode.BadRequest);
        Assert.Equal("tenant", tenant.GetProperty("parentRules").GetProperty("allowlist").GetString());

        // Narrowing further is fine, and the view shows what is inherited and from where.
        await SaveAsync(o.T, "layla", o.GulfHr, new { maxClassification = "public", maxOutputTokens = 500 });
        var view = await OkAsync(o.T["layla"].Client.GetAsync($"/api/v1/ai/org-policies/{o.GulfHr}"));
        Assert.Equal(Small, view.GetProperty("parent").GetProperty("allowlist")[0].GetString());
        Assert.Equal(o.Gulf.ToString(), view.GetProperty("source").GetProperty("allowlist").GetString());
        Assert.Equal("tenant", view.GetProperty("source").GetProperty("confidential").GetString());

        // A unit whose ceiling is Public refuses Internal content.
        Assert.Equal(AiDenials.ClassificationBlocked, (await ChatAsync(o.T, "layla", o.GulfHr)).Denial);
        Assert.True((await ChatAsync(o.T, "layla", o.GulfHr, classification: DataClassification.Public)).IsOk);
    }

    [Fact]
    public async Task A_unit_administrator_changes_only_their_subtree_and_a_save_over_an_older_version_is_refused()
    {
        var o = await FreshAsync();
        await SaveAsync(o.T, "gulfadmin", o.GulfHr, new { allowlist = new[] { Small } });
        Assert.Equal(HttpStatusCode.NotFound, (await o.T["gulfadmin"].Client.PutAsJsonAsync($"/api/v1/ai/org-policies/{o.Africa}", new { allowlist = new[] { Small } })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await o.T["gulfadmin"].Client.GetAsync("/api/v1/ai/policy")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await o.T["emp"].Client.GetAsync("/api/v1/ai/org-policies")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await o.T["emp"].Client.GetAsync($"/api/v1/ai/org-policies/{o.GulfHr}")).StatusCode);

        await SaveAsync(o.T, "layla", o.Africa, new { maxOutputTokens = 800 });
        var mine = await OkAsync(o.T["gulfadmin"].Client.GetAsync("/api/v1/ai/org-policies"));
        Assert.Equal([o.GulfHr], mine.EnumerateArray().Select(p => p.GetProperty("unitId").GetGuid()));
        Assert.Equal(2, (await OkAsync(o.T["layla"].Client.GetAsync("/api/v1/ai/org-policies"))).GetArrayLength());

        using var stale = new HttpRequestMessage(HttpMethod.Put, $"/api/v1/ai/org-policies/{o.GulfHr}") { Content = JsonContent.Create(new { allowlist = new[] { Small } }) };
        stale.Headers.TryAddWithoutValidation("If-Match", "\"v0\"");
        await OkAsync(o.T["gulfadmin"].Client.SendAsync(stale), HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Unit_policies_never_cross_tenants()
    {
        var a = await FreshAsync();
        var b = await FreshAsync();
        await SaveAsync(a.T, "layla", a.Gulf, new { allowlist = new[] { Small } });
        Assert.Equal(HttpStatusCode.NotFound, (await b.T["layla"].Client.GetAsync($"/api/v1/ai/org-policies/{a.Gulf}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await b.T["layla"].Client.PutAsJsonAsync($"/api/v1/ai/org-policies/{a.Gulf}", new { allowlist = new[] { Small } })).StatusCode);
        Assert.Equal(0, (await OkAsync(b.T["layla"].Client.GetAsync("/api/v1/ai/org-policies"))).GetArrayLength());
        Assert.Equal(Large, (await ChatAsync(b.T, "layla", b.Gulf)).Completion!.Model);
    }
}
