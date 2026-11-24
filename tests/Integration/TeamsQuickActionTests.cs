using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TeamBeats.Audit.Contracts;
using TeamBeats.Authorization.Contracts;
using TeamBeats.DevFakes;
using TeamBeats.Messaging;
using TeamBeats.Modules.Notifications;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Testing;
using Xunit;

namespace TeamBeats.Integration.Tests;

/// <summary>
/// Spec 015 US5 scenario 2 (R2, FR-018, section 14): approve and reject from Teams. An approver with Teams on and the
/// TeamBeats bot installed gets a card with approve, reject and a reason field; pressing a button runs the same task
/// decision as the inbox, as that person, with the same checks. Each button is signed for one person and message,
/// works once and for 7 days, and only from that person's Teams account in the tenant's directory. A tenant whose
/// sign-in rules Teams cannot show (MFA, trusted networks) finishes the action in TeamBeats. Without the bot the plain
/// activity notification goes out. Bot Framework and Graph are the fake of the test factory.
/// </summary>
[Collection(WorldCollection.Name)]
public sealed class TeamsQuickActionTests(TenantWorld world)
{
    private const string Admin = "/api/v1/services/admin";

    private sealed record Setup(TestTenant T, Guid Directory, string OmarOid, string LaylaOid, Guid Service, Guid OmarRole);

    private static Dictionary<string, string> Names(string en, string ar) => new() { ["en"] = en, ["ar"] = ar };

    private async Task<Setup> SetupAsync(bool installBot = true)
    {
        var t = await world.CreateTenantAsync("tq-" + Guid.NewGuid().ToString("N")[..8], "workplace_plus");
        var holding = await world.CreateUnitAsync(t, "Holding", null, "organization");
        var dubai = await world.CreateUnitAsync(t, "Dubai", holding, "department");
        foreach (var name in new[] { "sara", "omar", "layla" })
        {
            await world.SeedUserAsync(t, name, MembershipTypes.Employee);
            await world.AddMembershipAsync(t, name, "Dubai");
        }
        var omarRole = await world.AssignAsync(t, "omar", BuiltInRoles.Approver, "org_subtree", dubai);
        await world.AssignAsync(t, "layla", BuiltInRoles.Approver, "org_subtree", dubai);

        var directory = Guid.NewGuid();
        var admin = t["admin"].Client;
        var created = await admin.PostAsJsonAsync("/api/v1/integrations/connections", new
        {
            kind = "microsoft365", externalTenantId = directory, displayName = "Contoso Microsoft 365", features = new[] { "teams" },
        });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var connection = (await created.ReadAsync<JsonElement>()).GetProperty("id").GetGuid();
        world.Factory.Graph.Consent(directory, ["TeamsActivity.Send"]);
        var start = await admin.GetAsync($"/api/v1/integrations/connections/{connection}/features/teams/consent");
        var query = FakeIdentityProvider.ParseQuery(start.Headers.Location!.Query);
        var relay = await world.Factory.PlatformClient().GetAsync(
            $"/api/v1/integrations/consent/callback?admin_consent=True&tenant={directory:D}&state={Uri.EscapeDataString(query["state"])}");
        var client = world.Factory.TenantClient(t.Key, t["admin"].Token);
        client.DefaultRequestHeaders.Add("Cookie", start.Headers.GetValues("Set-Cookie").Select(v => v.Split(';')[0]).First(v => v.StartsWith("tb_consent=", StringComparison.Ordinal)));
        await client.GetAsync(relay.Headers.Location!.PathAndQuery);

        string Link(string name)
        {
            var oid = Guid.NewGuid().ToString("D");
            world.Database.OwnerExecInTenantAsync(t.Id,
                "insert into identity.external_identity (tenant_id, id, issuer, subject, user_id) values (@t, @id, @i, @s, @u)",
                ("t", t.Id), ("id", Guid.NewGuid()), ("i", $"https://login.microsoftonline.com/{directory:D}/v2.0"), ("s", oid), ("u", t[name].Id)).GetAwaiter().GetResult();
            if (installBot)
            {
                world.Factory.Graph.InstallTeamsApp(directory, oid);
            }
            return oid;
        }
        var omar = Link("omar");
        var layla = Link("layla");
        foreach (var name in new[] { "omar", "layla" })
        {
            var prefs = await t[name].Client.PutAsJsonAsync("/api/v1/me/notification-preferences",
                new { categories = new[] { new { category = "approval_waiting", email = false, digest = false, teams = true } } });
            Assert.Equal(HttpStatusCode.OK, prefs.StatusCode);
        }

        var service = await admin.PostAsJsonAsync(Admin, new { kind = "form", names = Names("Business cards", "بطاقات العمل") });
        var serviceId = (await service.ReadAsync<JsonElement>()).GetProperty("service").GetProperty("id").GetGuid();
        var form = new { fields = new object[] { new { key = "boxes", type = "number", labels = Names("Boxes", "العلب"), required = true } } };
        var route = new { approval = new { names = Names("Logistics approval", "موافقة اللوجستيات"), approvers = new { kind = "users", users = new[] { t["omar"].Id, t["layla"].Id } } } };
        Assert.Equal(HttpStatusCode.OK, (await admin.PutAsJsonAsync($"{Admin}/{serviceId}/draft", new { form, route })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsJsonAsync($"{Admin}/{serviceId}/publish", new { })).StatusCode);
        return new Setup(t, directory, omar, layla, serviceId, omarRole);
    }

    private async Task DispatchAsync()
    {
        var dispatcher = world.Factory.Services.GetRequiredService<OutboxDispatcher>();
        await dispatcher.DispatchBatchAsync(500, CancellationToken.None);
        await dispatcher.DispatchBatchAsync(500, CancellationToken.None);
    }

    private Task<int> DeliverAsync(TestTenant t) =>
        world.Factory.InScopeAsync(t.Id, async sp =>
        {
            var count = await sp.GetRequiredService<TeamsDelivery>().DeliverDueAsync(DateTimeOffset.UtcNow, 100, CancellationToken.None);
            await sp.GetRequiredService<IDbSession>().CommitAsync();
            return count;
        });

    /// <summary>Sara asks; the card goes to Omar and Layla. Returns the request, the task and the buttons of Omar's card.</summary>
    private async Task<(Guid Request, Guid Task, Dictionary<string, string> Buttons, JsonElement Card)> RequestAsync(Setup s)
    {
        var submitted = await s.T["sara"].Client.PostAsJsonAsync("/api/v1/requests", new { serviceId = s.Service, data = new { boxes = 2 } });
        Assert.Equal(HttpStatusCode.Created, submitted.StatusCode);
        var request = (await submitted.ReadAsync<JsonElement>()).GetProperty("request").GetProperty("id").GetGuid();
        await DispatchAsync();
        Assert.True(await DeliverAsync(s.T) >= 2);
        var task = (await (await s.T["omar"].Client.GetAsync("/api/v1/tasks")).ReadAsync<JsonElement>()).EnumerateArray()
            .Single(i => i.GetProperty("requestId").GetGuid() == request).GetProperty("taskId").GetGuid();
        var card = world.Factory.Graph.BotMessages.Where(m => m.UserId == s.OmarOid).Select(m => m.Card)
            .Single(c => c.GetProperty("actions").EnumerateArray().Any(a => a.GetProperty("type").GetString() == "Action.Execute"));
        var buttons = card.GetProperty("actions").EnumerateArray().Where(a => a.GetProperty("type").GetString() == "Action.Execute")
            .ToDictionary(a => a.GetProperty("verb").GetString()!, a => a.GetProperty("data").GetProperty("tb").GetString()!);
        return (request, task, buttons, card);
    }

    private async Task<HttpResponseMessage> PressAsync(string body, string? bearer)
    {
        using var message = new HttpRequestMessage(HttpMethod.Post, TeamBeats.Modules.Integrations.Microsoft365.TeamsBotEndpoint.Path) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        if (bearer is not null)
        {
            message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        }
        return await world.Factory.PlatformClient().SendAsync(message);
    }

    private async Task<string> ReplyTextAsync(Setup s, string oid, string verb, string token, string? reason = null, Guid? directory = null)
    {
        var response = await PressAsync(FakeMicrosoftGraph.BotInvoke(directory ?? s.Directory, oid, verb, token, reason), world.Factory.Graph.IssueBotToken());
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        var body = await response.ReadAsync<JsonElement>();
        Assert.Equal("application/vnd.microsoft.card.adaptive", body.GetProperty("type").GetString());
        return body.GetProperty("value").GetProperty("body")[0].GetProperty("text").GetString()!;
    }

    private async Task<string> StatusAsync(Setup s, Guid request) =>
        (await (await s.T["sara"].Client.GetAsync($"/api/v1/requests/{request}")).ReadAsync<JsonElement>()).GetProperty("request").GetProperty("status").GetString()!;

    private string Text(string key) => world.Factory.Services.GetRequiredService<TeamBeats.Localization.ILocalizer>().Get(key, "en");

    [Fact]
    public async Task Approving_from_Teams_runs_the_inbox_decision_as_the_person_once()
    {
        var s = await SetupAsync();
        var (request, task, buttons, card) = await RequestAsync(s);
        Assert.Equal(["approve", "reject"], buttons.Keys.Order().ToArray());
        Assert.Contains(card.GetProperty("body").EnumerateArray(), b => b.GetProperty("type").GetString() == "Input.Text" && b.GetProperty("id").GetString() == "reason");
        var open = card.GetProperty("actions").EnumerateArray().Single(a => a.GetProperty("type").GetString() == "Action.OpenUrl");
        Assert.Equal($"https://{s.T.Key}.{ApiFactory.BaseDomain}/requests/{request}?task={task}", open.GetProperty("url").GetString());
        // The tokens are kept only as hashes.
        Assert.Equal(0, await world.Database.CountInTenantAsync(s.T.Id, "select count(*) from notifications.quick_action where token_hash = @t", ("t", buttons["approve"])));

        Assert.Equal(Text("notifications.teams.quick.done"), await ReplyTextAsync(s, s.OmarOid, "approve", buttons["approve"]));
        Assert.Equal("approved", await StatusAsync(s, request));
        var tracking = (await (await s.T["sara"].Client.GetAsync($"/api/v1/requests/{request}")).ReadAsync<JsonElement>()).GetProperty("tracking").EnumerateArray().Last();
        Assert.Equal(s.T["omar"].Id, tracking.GetProperty("actor").GetProperty("id").GetGuid());
        await world.Database.AssertAuditedAsync(s.T.Id, AuditActions.TaskApproved, task.ToString());
        await world.Database.AssertAuditedAsync(s.T.Id, AuditActions.NotificationQuickActionUsed);

        // A button works once, and the other button of the same card is spent too.
        Assert.Equal(Text("notifications.teams.quick.expired"), await ReplyTextAsync(s, s.OmarOid, "approve", buttons["approve"]));
        Assert.Equal(Text("notifications.teams.quick.expired"), await ReplyTextAsync(s, s.OmarOid, "reject", buttons["reject"], "Too many"));
    }

    [Fact]
    public async Task Rejecting_needs_a_reason_and_a_decided_task_is_closed_for_the_other_approver()
    {
        var s = await SetupAsync();
        var (request, _, buttons, _) = await RequestAsync(s);
        Assert.Equal(Text("notifications.teams.quick.reason_required"), await ReplyTextAsync(s, s.OmarOid, "reject", buttons["reject"]));
        Assert.Equal("in_approval", await StatusAsync(s, request));
        Assert.Equal(Text("notifications.teams.quick.done"), await ReplyTextAsync(s, s.OmarOid, "reject", buttons["reject"], "Budget is spent"));
        Assert.Equal("rejected", await StatusAsync(s, request));

        // Layla's card for the same task: the task is no longer waiting for her.
        var layla = world.Factory.Graph.BotMessages.Where(m => m.UserId == s.LaylaOid).Select(m => m.Card).Last();
        var approve = layla.GetProperty("actions").EnumerateArray().First(a => a.GetProperty("verb").ValueKind == JsonValueKind.String && a.GetProperty("verb").GetString() == "approve");
        Assert.Equal(Text("notifications.teams.quick.closed"), await ReplyTextAsync(s, s.LaylaOid, "approve", approve.GetProperty("data").GetProperty("tb").GetString()!));
    }

    [Fact]
    public async Task Forged_foreign_expired_and_revoked_presses_do_nothing()
    {
        var s = await SetupAsync();
        var (request, _, buttons, _) = await RequestAsync(s);
        var body = FakeMicrosoftGraph.BotInvoke(s.Directory, s.OmarOid, "approve", buttons["approve"]);

        // Not Bot Framework: no token, another issuer or audience, an expired token, or a token for another service address.
        Assert.Equal(HttpStatusCode.Unauthorized, (await PressAsync(body, null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await PressAsync(body, world.Factory.Graph.IssueBotToken(issuer: "https://evil.example"))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await PressAsync(body, world.Factory.Graph.IssueBotToken(audience: Guid.NewGuid().ToString()))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await PressAsync(body, world.Factory.Graph.IssueBotToken(lifetime: TimeSpan.FromMinutes(-10)))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await PressAsync(body, world.Factory.Graph.IssueBotToken(serviceUrl: "https://evil.example/teams/"))).StatusCode);
        // The bot listens on the platform host only.
        var onTenantHost = await world.Factory.TenantClient(s.T.Key).PostAsync(TeamBeats.Modules.Integrations.Microsoft365.TeamsBotEndpoint.Path, new StringContent(body, Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.NotFound, onTenantHost.StatusCode);

        // Someone else's Teams account, another directory, a made up token or a tenant that does not exist.
        Assert.Equal(Text("notifications.teams.quick.expired"), await ReplyTextAsync(s, s.LaylaOid, "approve", buttons["approve"]));
        Assert.Equal(Text("notifications.teams.quick.expired"), await ReplyTextAsync(s, s.OmarOid, "approve", buttons["approve"], directory: Guid.NewGuid()));
        Assert.Equal(Text("notifications.teams.quick.expired"), await ReplyTextAsync(s, s.OmarOid, "reject", buttons["approve"], "Mixed up"));
        Assert.Equal(Text("notifications.teams.quick.expired"), await ReplyTextAsync(s, s.OmarOid, "approve", s.T.Id.ToString("N") + ".not-a-real-secret"));
        var other = (await PressAsync(FakeMicrosoftGraph.BotInvoke(s.Directory, s.OmarOid, "approve", Guid.NewGuid().ToString("N") + ".x"), world.Factory.Graph.IssueBotToken()));
        Assert.Equal(HttpStatusCode.OK, other.StatusCode);
        Assert.Equal("in_approval", await StatusAsync(s, request));

        // Seven days later the button has expired.
        await world.Database.OwnerExecInTenantAsync(s.T.Id, "update notifications.quick_action set expires_at = now() - interval '1 minute'");
        Assert.Equal(Text("notifications.teams.quick.expired"), await ReplyTextAsync(s, s.OmarOid, "approve", buttons["approve"]));
        Assert.Equal("in_approval", await StatusAsync(s, request));

        // Fresh buttons, but Omar lost the approver role: the inbox check refuses him from Teams as well.
        await world.Database.OwnerExecInTenantAsync(s.T.Id, "update notifications.quick_action set expires_at = now() + interval '1 day'");
        Assert.True((await s.T["admin"].Client.DeleteAsync($"/api/v1/authz/assignments/{s.OmarRole}")).IsSuccessStatusCode);
        Assert.Equal(Text("notifications.teams.quick.denied"), await ReplyTextAsync(s, s.OmarOid, "approve", buttons["approve"]));
        Assert.Equal("in_approval", await StatusAsync(s, request));
    }

    [Fact]
    public async Task A_tenant_that_requires_MFA_finishes_the_action_in_TeamBeats()
    {
        var s = await SetupAsync();
        var (request, _, buttons, _) = await RequestAsync(s);
        await world.Database.OwnerExecInTenantAsync(s.T.Id,
            "insert into security.mfa_claim_mapping (tenant_id, provider, seen_at) values (current_setting('app.tenant_id')::uuid, 'entra', now()) on conflict do nothing");
        var policy = await s.T["admin"].Client.PutAsJsonAsync("/api/v1/security/policy", new { requireMfa = true });
        Assert.True(policy.IsSuccessStatusCode, await policy.Content.ReadAsStringAsync());

        Assert.Equal(Text("notifications.teams.quick.step_up"), await ReplyTextAsync(s, s.OmarOid, "approve", buttons["approve"]));
        Assert.Equal("in_approval", await StatusAsync(s, request));
    }

    [Fact]
    public async Task Without_the_bot_the_approver_gets_the_activity_notification()
    {
        var s = await SetupAsync();
        world.Factory.Graph.BotRefused = true;
        try
        {
            var submitted = await s.T["sara"].Client.PostAsJsonAsync("/api/v1/requests", new { serviceId = s.Service, data = new { boxes = 1 } });
            Assert.Equal(HttpStatusCode.Created, submitted.StatusCode);
            await DispatchAsync();
            Assert.True(await DeliverAsync(s.T) >= 2);
        }
        finally
        {
            world.Factory.Graph.BotRefused = false;
        }
        Assert.DoesNotContain(world.Factory.Graph.BotMessages, m => m.Directory == s.Directory.ToString("D"));
        Assert.Contains(world.Factory.Graph.TeamsActivities, a => a.Directory == s.Directory.ToString("D") && a.UserId == s.OmarOid);
        Assert.Equal(0, await world.Database.CountInTenantAsync(s.T.Id, "select count(*) from notifications.quick_action q join notifications.delivery d on d.notification_id = q.notification_id where d.status <> 'sent'"));
    }
}
