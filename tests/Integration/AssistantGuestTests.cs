using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TeamBeats.Audit.Contracts;
using TeamBeats.Authorization.Contracts;
using TeamBeats.Messaging;
using TeamBeats.Modules.Agents.Contracts;
using TeamBeats.Modules.Identity;
using TeamBeats.Modules.Search.Contracts;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Testing;
using Xunit;

namespace TeamBeats.Integration.Tests;

/// <summary>
/// Spec 014 R2 slice 1 (T009, T010): guests are refused by the assistant unless the tenant lets them use it, and then
/// only the spaces they were invited to can be cited (AST-022); payslips and anonymous engagement content never reach a
/// model (AST-020).
/// </summary>
[Collection(WorldCollection.Name)]
public sealed class AssistantGuestTests(TenantWorld world) : IAsyncLifetime
{
    private DevHostFactory _dev = null!;

    public Task InitializeAsync()
    {
        _dev = new DevHostFactory(world.Database);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync() => await _dev.DisposeAsync();

    /// <summary>An invited guest's first sign-in makes them a guest of the space; the client then talks to the test host, whose AI is faked.</summary>
    private async Task<HttpClient> SignInAsync(TestTenant t, string email)
    {
        var response = await _dev.Tenant(t.Key).PostAsJsonAsync("/dev/signin", new { email });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var me = await OkAsync(_dev.Tenant(t.Key, DevHostFactory.SessionToken(response)).GetAsync("/api/v1/me"));
        var id = me.GetProperty("id").GetGuid();
        var token = await world.Factory.InScopeAsync(t.Id, async sp =>
        {
            var value = await sp.GetRequiredService<SessionService>().CreateUserSessionAsync(id, "test", CancellationToken.None);
            await sp.GetRequiredService<IDbSession>().CommitAsync();
            return value;
        });
        return world.Factory.SeededUserClient(t.Key, t.Id, token);
    }

    private const string Assistant = "/api/v1/assistant";
    private const string Spaces = "/api/v1/spaces";

    private static async Task<JsonElement> OkAsync(Task<HttpResponseMessage> call, HttpStatusCode expected = HttpStatusCode.OK)
    {
        var response = await call;
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == expected, $"{(int)response.StatusCode}: {text}");
        return text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone();
    }

    private async Task DispatchAsync()
    {
        var dispatcher = world.Factory.Services.GetRequiredService<OutboxDispatcher>();
        for (var i = 0; i < 20 && await dispatcher.DispatchBatchAsync(500, CancellationToken.None) > 0; i++)
        {
        }
    }

    private static async Task<Guid> SpaceAsync(TestTenant t, string name) =>
        (await OkAsync(t["omar"].Client.PostAsJsonAsync(Spaces, new { name, description = $"The {name} space plans the warehouse scanner delivery.", visibility = "private", template = "project" }),
            HttpStatusCode.Created)).GetProperty("id").GetGuid();

    private static Task<JsonElement> AskAsync(HttpClient client, string question) =>
        OkAsync(client.PostAsJsonAsync($"{Assistant}/turns", new { question }));

    [Fact]
    public async Task Guests_are_refused_unless_allowed_and_then_cite_only_their_spaces()
    {
        var t = await world.CreateTenantAsync("ag-" + Guid.NewGuid().ToString("N")[..8], "workplace_plus", "en", ["en", "ar"]);
        await world.SeedUserAsync(t, "omar", MembershipTypes.Employee);
        await world.SeedUserAsync(t, "aip", MembershipTypes.Employee);
        await world.AssignAsync(t, "aip", BuiltInRoles.AiPolicyAdministrator, "tenant", null);
        await OkAsync(t["admin"].Client.PutAsJsonAsync($"{Spaces}/manage/settings", new { creationUnits = Array.Empty<Guid>(), guestsAllowed = true, guestDays = 60, version = 0 }));
        var invited = await SpaceAsync(t, "Vendor rollout");
        var hidden = await SpaceAsync(t, "Budget review");
        await OkAsync(t["omar"].Client.PostAsJsonAsync($"{Spaces}/{invited}/guests", new { email = $"gina@partner-{t.Key}.example", role = "member", days = 30 }));
        var gina = await SignInAsync(t, $"gina@partner-{t.Key}.example");
        await DispatchAsync();

        // Off by default: every assistant use is refused, but a guest can still delete their own conversations.
        Assert.False((await OkAsync(t["aip"].Client.GetAsync($"{Assistant}/settings"))).GetProperty("guestsAllowed").GetBoolean());
        Assert.Equal(HttpStatusCode.Forbidden, (await gina.PostAsJsonAsync($"{Assistant}/turns", new { question = "What does the vendor rollout space plan?" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await gina.PostAsJsonAsync($"{Assistant}/actions", new { action = "summarize", text = "Plan the delivery." })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await gina.GetAsync($"{Assistant}/conversations")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await gina.PostAsJsonAsync($"{Assistant}/turns/{Guid.NewGuid()}/feedback", new { helpful = true })).StatusCode);
        await OkAsync(gina.DeleteAsync($"{Assistant}/conversations"));
        Assert.Equal(0L, await world.Database.CountInTenantAsync(t.Id, "select count(*) from agents.turn"));

        // An employee is not affected.
        Assert.Equal(AssistantOutcomes.Answered, (await AskAsync(t["omar"].Client, "What does the vendor rollout space plan?")).GetProperty("outcome").GetString());

        // Only the AI Policy Administrator turns it on; the change is audited and the retention is kept.
        Assert.Equal(HttpStatusCode.Forbidden, (await t["omar"].Client.PutAsJsonAsync($"{Assistant}/settings", new { guestsAllowed = true })).StatusCode);
        var settings = await OkAsync(t["aip"].Client.PutAsJsonAsync($"{Assistant}/settings", new { guestsAllowed = true }));
        Assert.True(settings.GetProperty("guestsAllowed").GetBoolean());
        Assert.Equal(30, settings.GetProperty("retentionDays").GetInt32());
        await world.Database.AssertAuditedAsync(t.Id, AuditActions.AssistantGuestsChanged, "guests");
        Assert.Equal(0L, await world.Database.CountInTenantAsync(t.Id, "select count(*) from audit.audit_event where action = @action",
            ("action", AuditActions.AssistantRetentionChanged)));

        // On, retrieval runs as the guest: the space they were invited to is cited, another private space never is.
        var turn = await AskAsync(gina, "What does the vendor rollout space plan?");
        Assert.True(turn.GetProperty("outcome").GetString() == AssistantOutcomes.Answered, turn.ToString());
        var cited = turn.GetProperty("citations").EnumerateArray().Select(c => c.GetProperty("itemId").GetGuid()).ToList();
        Assert.Contains(invited, cited);
        Assert.DoesNotContain(hidden, cited);
        var budget = await AskAsync(gina, "What does the budget review space plan?");
        Assert.DoesNotContain(hidden, budget.GetProperty("citations").EnumerateArray().Select(c => c.GetProperty("itemId").GetGuid()));
        Assert.Equal(2, (await OkAsync(gina.GetAsync($"{Assistant}/conversations"))).GetProperty("conversations").GetArrayLength());

        // The setting is the tenant's own.
        var other = await world.CreateTenantAsync("ag-o-" + Guid.NewGuid().ToString("N")[..6], "workplace_plus");
        Assert.False((await OkAsync(other["admin"].Client.GetAsync($"{Assistant}/settings"))).GetProperty("guestsAllowed").GetBoolean());
    }

    [Fact]
    public async Task Payslips_and_anonymous_engagement_are_never_a_search_source()
    {
        // No owner registers a denied source, and the indexer would refuse one that did (AST-020).
        await using var scope = world.Factory.Services.CreateAsyncScope();
        var sources = scope.ServiceProvider.GetServices<ISearchSource>().Select(s => s.Source).ToList();
        Assert.NotEmpty(sources);
        Assert.DoesNotContain(sources, AiExcludedSources.Contains);

        // A unit stored under a denied source by mistake is still never retrieved for a model.
        var t = await world.CreateTenantAsync("ag-x-" + Guid.NewGuid().ToString("N")[..6], "workplace_plus");
        var item = Guid.NewGuid();
        await world.Database.OwnerExecInTenantAsync(t.Id,
            """
            insert into search.unit (tenant_id, id, source, item_id, unit_key, position, kind, locale, title, body, link, classification, synced_at, tsv)
            values (current_setting('app.tenant_id')::uuid, gen_random_uuid(), @source, @item, 'pay', 0, 'document', 'en', 'March payslip', 'Net pay for March', '/pay', 'internal', now(),
                    to_tsvector('simple', 'March payslip net pay'))
            """, ("source", AiExcludedSources.Payslip), ("item", item));
        var retrieved = await world.Factory.AsUserAsync(t.Id, t["admin"].Id, sp =>
            sp.GetRequiredService<ISearchRetrieval>().ItemAsync(AiExcludedSources.Payslip, item, "assistant"));
        Assert.Empty(retrieved.Passages);
        Assert.Equal(0, retrieved.Withheld);
    }
}
