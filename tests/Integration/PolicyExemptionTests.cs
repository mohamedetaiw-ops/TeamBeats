using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TeamBeats.Audit.Contracts;
using TeamBeats.Authorization.Contracts;
using TeamBeats.Messaging;
using TeamBeats.Modules.Policies;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Persistence;
using TeamBeats.TenantContext;
using TeamBeats.Testing;
using Xunit;

namespace TeamBeats.Integration.Tests;

/// <summary>
/// Spec 025 T015 (US7, FR-014): a person asks for an exemption with a reason and an end date; the request goes to the
/// policy owner, then the compliance officers, in the inbox, never to the requester or the person; approved exemptions
/// are the register and show on the person's compliance view; 14 days before the end the person and the owner hear of
/// it and on the end date it closes on its own; a compliance officer may ask for someone else or a unit; requests can be
/// withdrawn and rejected; packages without <c>policy.exemptions</c> and other tenants reach nothing.
/// </summary>
[Collection(WorldCollection.Name)]
public sealed class PolicyExemptionTests(TenantWorld world)
{
    private const string Policies = "/api/v1/policies";

    private sealed record Org(TestTenant T, Guid Gulf, string Owner);

    private async Task<Org> FreshAsync(string package = "workplace_plus")
    {
        var t = await world.CreateTenantAsync("pex-" + Guid.NewGuid().ToString("N")[..8], package, "en", ["en", "ar"]);
        var holding = await world.CreateUnitAsync(t, "Holding", null, "organization");
        var gulf = await world.CreateUnitAsync(t, "Gulf", holding, "subsidiary");
        foreach (var name in new[] { "co", "co2", "sara", "legal", "ali", "bea" })
        {
            await world.SeedUserAsync(t, name, MembershipTypes.Employee);
            await world.AddMembershipAsync(t, name, "Gulf");
        }
        await world.AssignAsync(t, "co", BuiltInRoles.ComplianceOfficer, "tenant", null);
        await world.AssignAsync(t, "co2", BuiltInRoles.ComplianceOfficer, "tenant", null);
        if (package == "workplace")
        {
            return new Org(t, gulf, "co"); // Custom roles are not in this package; the compliance officer owns the policy.
        }
        var role = await t["admin"].Client.PostAsJsonAsync("/api/v1/authz/roles", new
        {
            key = "policy_writer", names = new Dictionary<string, string> { ["en"] = "Policy writer" }, permissions = new[] { Permissions.PolicyAuthor }, delegable = true,
        });
        Assert.Equal(HttpStatusCode.Created, role.StatusCode);
        await world.AssignAsync(t, "sara", "policy_writer", "tenant", null);
        return new Org(t, gulf, "sara");
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

    private static Guid Id(JsonElement e) => e.GetProperty("id").GetGuid();

    private static Dictionary<string, string> Both(string text) => new() { ["en"] = text, ["ar"] = text + " ع" };

    private static string Day(int days) => DateOnly.FromDateTime(DateTime.UtcNow).AddDays(days).ToString("yyyy-MM-dd");

    /// <summary>A policy owned by Sara at Gulf, in force after Legal approved it.</summary>
    private static async Task<Guid> PublishedAsync(Org o)
    {
        var t = o.T;
        var id = Id(await OkAsync(t["co"].Client.PostAsJsonAsync(Policies, new
        {
            code = "RA", names = Both("Remote access"), ownerId = t[o.Owner].Id, orgUnitId = o.Gulf, route = new[] { new { approvers = new[] { t["legal"].Id } } },
        }), HttpStatusCode.Created));
        var clause = new
        {
            level = 1, titles = Both("Access"),
            bodies = new Dictionary<string, object>
            {
                ["en"] = new { blocks = new object[] { new { type = "paragraph", children = new[] { new { text = "Use the VPN." } } } } },
                ["ar"] = new { blocks = new object[] { new { type = "paragraph", children = new[] { new { text = "استخدم الشبكة." } } } } },
            },
        };
        await OkAsync(t["co"].Client.PutAsJsonAsync($"{Policies}/{id}/draft", new { titles = Both("Remote access"), clauses = new[] { clause } }));
        await OkAsync(t["co"].Client.PostAsJsonAsync($"{Policies}/{id}/submit", new { }));
        await DecideAsync(t, "legal", "policy_approval", "approve");
        return id;
    }

    private static async Task<JsonElement[]> TasksAsync(TestTenant t, string user, string source) =>
        (await OkAsync(t[user].Client.GetAsync("/api/v1/tasks"))).EnumerateArray().Where(i => i.GetProperty("source").GetString() == source).ToArray();

    private static async Task DecideAsync(TestTenant t, string user, string source, string outcome)
    {
        var task = (await TasksAsync(t, user, source)).Single();
        await OkAsync(t[user].Client.PostAsJsonAsync($"/api/v1/tasks/{task.GetProperty("taskId").GetGuid()}/decision", new { outcome, comment = "Decided in the test" }));
    }

    private Task<int> RunDueAsync(TestTenant t, DateTimeOffset now) =>
        world.Factory.InScopeAsync(t.Id, async sp =>
        {
            sp.GetRequiredService<TenantContextAccessor>().SetActor(ActorType.System, null);
            var moved = await sp.GetRequiredService<PolicyExemptions>().RunDueAsync(now, 200, CancellationToken.None);
            await sp.GetRequiredService<IDbSession>().CommitAsync();
            return moved;
        });

    private async Task DispatchUntilAsync(Func<Task<bool>> done)
    {
        var dispatcher = world.Factory.Services.GetRequiredService<OutboxDispatcher>();
        for (var i = 0; i < 50 && !await done(); i++)
        {
            await dispatcher.DispatchBatchAsync(500, CancellationToken.None);
        }
        Assert.True(await done());
    }

    private Task<long> NotificationsAsync(TestTenant t, string user, string template) =>
        world.Database.CountInTenantAsync(t.Id, "select count(*) from notifications.notification where user_id = @u and template_key = @k", ("u", t[user].Id), ("k", template));

    [Fact]
    public async Task A_request_goes_to_the_owner_then_compliance_lands_in_the_register_reminds_and_expires()
    {
        var o = await FreshAsync();
        var t = o.T;
        var policy = await PublishedAsync(o);
        var path = $"{Policies}/{policy}/exemptions";
        Assert.True((await OkAsync(t["ali"].Client.GetAsync($"{Policies}/{policy}"))).GetProperty("rights").GetProperty("exemptions").GetBoolean());
        var rights = (await OkAsync(t["ali"].Client.GetAsync(path))).GetProperty("rights");
        Assert.True(rights.GetProperty("request").GetBoolean());
        Assert.False(rights.GetProperty("requestFor").GetBoolean());
        await OkAsync(t["co"].Client.PostAsJsonAsync($"{Policies}/{policy}/campaigns", new { }), HttpStatusCode.Created);

        // A reason and an end date from today and within a year; only for oneself unless a compliance officer asks.
        Assert.Contains("validation.policies.exemption_reason", await ErrorKeysAsync(await t["ali"].Client.PostAsJsonAsync(path, new { endsOn = Day(30) })));
        Assert.Contains("validation.policies.exemption_ends_on", await ErrorKeysAsync(await t["ali"].Client.PostAsJsonAsync(path, new { reason = "Travel", endsOn = Day(400) })));
        Assert.Contains("validation.policies.exemption_ends_on", await ErrorKeysAsync(await t["ali"].Client.PostAsJsonAsync(path, new { reason = "Travel", endsOn = Day(-3) })));
        Assert.Contains("validation.policies.exemption_scope", await ErrorKeysAsync(await t["ali"].Client.PostAsJsonAsync(path, new { scope = "team", reason = "Travel", endsOn = Day(30) })));
        Assert.Equal(HttpStatusCode.Forbidden, (await t["ali"].Client.PostAsJsonAsync(path, new { userId = t["bea"].Id, reason = "Travel", endsOn = Day(30) })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await t["ali"].Client.PostAsJsonAsync(path, new { scope = "unit", unitId = o.Gulf, reason = "Travel", endsOn = Day(30) })).StatusCode);

        var asked = await OkAsync(t["ali"].Client.PostAsJsonAsync(path, new { reason = "Working from a client site without the VPN", endsOn = Day(60) }), HttpStatusCode.Created);
        var exemption = Id(asked);
        Assert.Equal("pending", asked.GetProperty("status").GetString());
        Assert.Equal("person", asked.GetProperty("scope").GetString());
        Assert.Equal(t["ali"].Id, asked.GetProperty("person").GetProperty("id").GetGuid());
        Assert.True(asked.GetProperty("canWithdraw").GetBoolean());
        await world.Database.AssertAuditedAsync(t.Id, AuditActions.PolicyExemptionRequested, policy.ToString());
        Assert.Contains("conflict.policies.exemption_exists", await ErrorKeysAsync(await t["ali"].Client.PostAsJsonAsync(path, new { reason = "Again", endsOn = Day(30) })));

        // The owner decides first, and hears of it; the compliance officers wait; the requester never sees the task.
        await DispatchUntilAsync(async () => await NotificationsAsync(t, "sara", PolicyExemptions.WaitingTemplate) == 1);
        Assert.Single(await TasksAsync(t, "sara", "policy_exemption"));
        Assert.Empty(await TasksAsync(t, "co", "policy_exemption"));
        Assert.Empty(await TasksAsync(t, "ali", "policy_exemption"));
        Assert.Equal($"/policies/{policy}/exemptions?exemption={exemption}", (await TasksAsync(t, "sara", "policy_exemption"))[0].GetProperty("link").GetString());
        await DecideAsync(t, "sara", "policy_exemption", "approve");
        Assert.Equal("pending", (await OkAsync(t["ali"].Client.GetAsync(path))).GetProperty("items")[0].GetProperty("status").GetString());
        await DecideAsync(t, "co", "policy_exemption", "approve");

        var mine = (await OkAsync(t["ali"].Client.GetAsync(path))).GetProperty("items");
        Assert.Equal("approved", mine[0].GetProperty("status").GetString());
        Assert.Equal(2, mine[0].GetProperty("approvals").GetArrayLength());
        Assert.Equal(t["co"].Id, mine[0].GetProperty("decidedBy").GetProperty("id").GetGuid());
        Assert.False(mine[0].GetProperty("canWithdraw").GetBoolean());
        await world.Database.AssertAuditedAsync(t.Id, AuditActions.PolicyExemptionApproved, policy.ToString());
        Assert.Equal(1, await NotificationsAsync(t, "ali", PolicyExemptions.ApprovedTemplate));

        // Others see only their own; the owner and compliance officers see the register.
        Assert.Equal(0, (await OkAsync(t["bea"].Client.GetAsync(path))).GetProperty("items").GetArrayLength());
        Assert.Equal(1, (await OkAsync(t["sara"].Client.GetAsync(path))).GetProperty("items").GetArrayLength());
        var registerList = await OkAsync(t["co"].Client.GetAsync($"{Policies}/exemptions"));
        Assert.Contains(registerList.EnumerateArray(), e => Id(e) == exemption);
        Assert.Equal(0, (await OkAsync(t["co"].Client.GetAsync($"{Policies}/exemptions?status=rejected"))).GetArrayLength());
        Assert.Contains("validation.policies.exemption_status", await ErrorKeysAsync(await t["co"].Client.GetAsync($"{Policies}/exemptions?status=maybe")));
        Assert.Equal(HttpStatusCode.Forbidden, (await t["ali"].Client.GetAsync($"{Policies}/exemptions")).StatusCode);

        // The person's compliance view and the people list show it.
        var person = await OkAsync(t["co"].Client.GetAsync($"{Policies}/compliance/people/{t["ali"].Id}"));
        Assert.Equal(exemption, Id(person.GetProperty("exemptions").EnumerateArray().Single()));
        var people = (await OkAsync(t["co"].Client.GetAsync($"{Policies}/{policy}/compliance/people"))).GetProperty("items").EnumerateArray().ToList();
        Assert.Equal(Day(60), people.Single(p => p.GetProperty("person").GetProperty("id").GetGuid() == t["ali"].Id).GetProperty("exemptUntil").GetString());
        Assert.Equal(JsonValueKind.Null, people.Single(p => p.GetProperty("person").GetProperty("id").GetGuid() == t["bea"].Id).GetProperty("exemptUntil").ValueKind);

        // 14 days before the end the person and the owner hear of it, once; on the end date it closes.
        var now = DateTimeOffset.UtcNow;
        Assert.Equal(0, await RunDueAsync(t, now));
        await world.Database.OwnerExecInTenantAsync(t.Id, "update policies.exemption set ends_at = @at where id = @id", ("at", now.AddDays(10)), ("id", exemption));
        Assert.Equal(1, await RunDueAsync(t, now));
        Assert.Equal(0, await RunDueAsync(t, now));
        Assert.Equal(1, await NotificationsAsync(t, "ali", PolicyExemptions.ExpiringTemplate));
        Assert.Equal(1, await NotificationsAsync(t, "sara", PolicyExemptions.ExpiringTemplate));
        Assert.Equal(1, await RunDueAsync(t, now.AddDays(11)));
        Assert.Equal("expired", (await OkAsync(t["ali"].Client.GetAsync(path))).GetProperty("items")[0].GetProperty("status").GetString());
        await world.Database.AssertAuditedAsync(t.Id, AuditActions.PolicyExemptionExpired, policy.ToString());
        Assert.Equal(1, await NotificationsAsync(t, "ali", PolicyExemptions.ExpiredTemplate));
        Assert.Equal(1, await NotificationsAsync(t, "sara", PolicyExemptions.ExpiredTemplate));
        Assert.Equal(0, (await OkAsync(t["co"].Client.GetAsync($"{Policies}/compliance/people/{t["ali"].Id}"))).GetProperty("exemptions").GetArrayLength());

        // Once it ended, a new request may be made.
        await OkAsync(t["ali"].Client.PostAsJsonAsync(path, new { reason = "Still at the client", endsOn = Day(20) }), HttpStatusCode.Created);
    }

    [Fact]
    public async Task Compliance_asks_for_a_unit_requests_are_rejected_and_withdrawn_and_the_owner_skips_their_own_step()
    {
        var o = await FreshAsync();
        var t = o.T;
        var policy = await PublishedAsync(o);
        var path = $"{Policies}/{policy}/exemptions";

        // A compliance officer asks for the whole unit: the owner, then another officer, never the requester.
        var unit = await OkAsync(t["co"].Client.PostAsJsonAsync(path, new { scope = "unit", unitId = o.Gulf, reason = "Branch migration", endsOn = Day(30) }), HttpStatusCode.Created);
        Assert.Equal(o.Gulf, unit.GetProperty("unitId").GetGuid());
        Assert.Equal("Gulf", unit.GetProperty("unitNames").GetProperty("en").GetString());
        Assert.Contains("validation.policies.org_unit", await ErrorKeysAsync(await t["co"].Client.PostAsJsonAsync(path,
            new { scope = "unit", unitId = Guid.NewGuid(), reason = "X", endsOn = Day(30) })));
        await DecideAsync(t, "sara", "policy_exemption", "approve");
        Assert.Empty(await TasksAsync(t, "co", "policy_exemption"));
        await DecideAsync(t, "co2", "policy_exemption", "reject");
        var rejected = (await OkAsync(t["co"].Client.GetAsync(path))).GetProperty("items").EnumerateArray().Single(e => Id(e) == Id(unit));
        Assert.Equal("rejected", rejected.GetProperty("status").GetString());
        await world.Database.AssertAuditedAsync(t.Id, AuditActions.PolicyExemptionRejected, policy.ToString());
        Assert.Equal(1, await NotificationsAsync(t, "co", PolicyExemptions.RejectedTemplate));

        // A compliance officer may ask for one person, who never decides it.
        var forBea = await OkAsync(t["co"].Client.PostAsJsonAsync(path, new { userId = t["bea"].Id, reason = "Medical", endsOn = Day(10) }), HttpStatusCode.Created);
        Assert.Equal(t["bea"].Id, forBea.GetProperty("person").GetProperty("id").GetGuid());
        Assert.Contains("validation.policies.exemption_person", await ErrorKeysAsync(await t["co"].Client.PostAsJsonAsync(path,
            new { userId = Guid.NewGuid(), reason = "X", endsOn = Day(10) })));
        Assert.Contains(Id(forBea), (await OkAsync(t["bea"].Client.GetAsync(path))).GetProperty("items").EnumerateArray().Select(Id));

        // The requester withdraws; the task leaves the owner's inbox; a closed request stays closed.
        var ali = Id(await OkAsync(t["ali"].Client.PostAsJsonAsync(path, new { reason = "Travel", endsOn = Day(15) }), HttpStatusCode.Created));
        Assert.Equal(HttpStatusCode.NotFound, (await t["bea"].Client.PostAsJsonAsync($"{path}/{ali}/withdraw", new { })).StatusCode);
        Assert.Equal(2, (await TasksAsync(t, "sara", "policy_exemption")).Length);
        var withdrawn = await OkAsync(t["ali"].Client.PostAsJsonAsync($"{path}/{ali}/withdraw", new { }));
        Assert.Equal("withdrawn", withdrawn.GetProperty("status").GetString());
        Assert.Single(await TasksAsync(t, "sara", "policy_exemption"));
        Assert.Contains("conflict.policies.exemption_closed", await ErrorKeysAsync(await t["ali"].Client.PostAsJsonAsync($"{path}/{ali}/withdraw", new { })));
        Assert.Equal(HttpStatusCode.NotFound, (await t["ali"].Client.PostAsJsonAsync($"{path}/{Guid.NewGuid()}/withdraw", new { })).StatusCode);

        // The owner asking for themselves goes straight to the compliance officers.
        await OkAsync(t["sara"].Client.PostAsJsonAsync(path, new { reason = "Owner travel", endsOn = Day(5) }), HttpStatusCode.Created);
        Assert.Single(await TasksAsync(t, "sara", "policy_exemption"));
        Assert.Single(await TasksAsync(t, "co", "policy_exemption"));
    }

    [Fact]
    public async Task Without_policy_exemptions_nothing_is_reachable_and_other_tenants_see_nothing()
    {
        var plain = await FreshAsync("workplace");
        var policy = await PublishedAsync(plain);
        Assert.False((await OkAsync(plain.T["ali"].Client.GetAsync($"{Policies}/{policy}"))).GetProperty("rights").GetProperty("exemptions").GetBoolean());
        Assert.Equal(HttpStatusCode.Forbidden, (await plain.T["ali"].Client.PostAsJsonAsync($"{Policies}/{policy}/exemptions", new { reason = "X", endsOn = Day(5) })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await plain.T["co"].Client.GetAsync($"{Policies}/exemptions")).StatusCode);

        var o = await FreshAsync();
        var own = await PublishedAsync(o);
        var exemption = Id(await OkAsync(o.T["ali"].Client.PostAsJsonAsync($"{Policies}/{own}/exemptions", new { reason = "Travel", endsOn = Day(5) }), HttpStatusCode.Created));
        var other = (await FreshAsync()).T;
        Assert.Equal(HttpStatusCode.NotFound, (await other["co"].Client.GetAsync($"{Policies}/{own}/exemptions")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other["co"].Client.PostAsJsonAsync($"{Policies}/{own}/exemptions/{exemption}/withdraw", new { })).StatusCode);
        Assert.DoesNotContain((await OkAsync(other["co"].Client.GetAsync($"{Policies}/exemptions"))).EnumerateArray(), e => Id(e) == exemption);
    }
}
