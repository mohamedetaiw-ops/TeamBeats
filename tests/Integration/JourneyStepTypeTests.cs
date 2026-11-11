using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TeamBeats.Audit.Contracts;
using TeamBeats.Authorization.Contracts;
using TeamBeats.Messaging;
using TeamBeats.Modules.Hr.Contracts;
using TeamBeats.Modules.Hr.Journeys;
using TeamBeats.Modules.Integrations.Contracts;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Persistence;
using TeamBeats.TenantContext;
using TeamBeats.Testing;
using Xunit;

namespace TeamBeats.Integration.Tests;

/// <summary>
/// Spec 022 T019 (US4 scenario 4, US5 scenarios 4 and 5, FR-015, FR-017, FR-018, with spec 025 FR-017): an attestation
/// step asks the new joiner to attest to every policy flagged "onboarding required" that applies to them and is done
/// when they have; an access removal step keeps a checklist per connected system, done when every line is confirmed and
/// escalated to IT and security officers after one working day; an exit interview step records structured reasons
/// counted with the minimum group size, and its free text reaches HR officers only. Other units and tenants see nothing.
/// </summary>
[Collection(WorldCollection.Name)]
public sealed class JourneyStepTypeTests(TenantWorld world)
{
    private const string Templates = "/api/v1/hr/journey-templates";
    private const string Journeys = "/api/v1/hr/journeys";
    private const string Policies = "/api/v1/policies";

    private static Dictionary<string, string> Names(string en, string ar) => new() { ["en"] = en, ["ar"] = ar };

    private async Task<(TestTenant T, Guid Gulf, Guid Egypt)> FreshAsync(params string[] extra)
    {
        var t = await world.CreateTenantAsync("js-" + Guid.NewGuid().ToString("N")[..8], "workplace_plus", "en", ["en", "ar"]);
        var holding = await world.CreateUnitAsync(t, "Holding", null, "organization");
        var gulf = await world.CreateUnitAsync(t, "Gulf", holding, "subsidiary");
        var egypt = await world.CreateUnitAsync(t, "Egypt", holding, "subsidiary");
        foreach (var name in new[] { "mona", "karim", "rana", "co", "legal", "hr_gulf" }.Concat(extra))
        {
            await world.SeedUserAsync(t, name, MembershipTypes.Employee);
            await world.AddMembershipAsync(t, name, "Gulf");
        }
        foreach (var name in new[] { "nadia", "hr_egypt" })
        {
            await world.SeedUserAsync(t, name, MembershipTypes.Employee);
            await world.AddMembershipAsync(t, name, "Egypt");
        }
        await world.AssignAsync(t, "hr_gulf", BuiltInRoles.HrOfficer, "org_subtree", gulf);
        await world.AssignAsync(t, "hr_egypt", BuiltInRoles.HrOfficer, "org_subtree", egypt);
        await world.AssignAsync(t, "co", BuiltInRoles.ComplianceOfficer, "tenant", null);
        return (t, gulf, egypt);
    }

    private Task<DateOnly> TodayAsync(TestTenant t) =>
        world.Factory.InScopeAsync(t.Id, async sp =>
        {
            var zone = TimeZoneInfo.FindSystemTimeZoneById((await sp.GetRequiredService<ITenantSettingsReader>().GetLocaleSettingsAsync()).TimeZone);
            return DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, zone));
        });

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

    private Task HandleAsync<TEvent>(TestTenant t, TEvent message) where TEvent : TeamBeats.Primitives.IIntegrationEvent =>
        world.Factory.InScopeAsync(t.Id, async sp =>
        {
            await sp.GetRequiredService<JourneyStarter>().HandleAsync((dynamic)message, CancellationToken.None);
            await sp.GetRequiredService<IDbSession>().CommitAsync();
            return 0;
        });

    private Task<int> WorkAsync(TestTenant t, Func<JourneyService, Task<int>> work) =>
        world.Factory.InScopeAsync(t.Id, async sp =>
        {
            sp.GetRequiredService<TenantContextAccessor>().SetActor(ActorType.System, null);
            var result = await work(sp.GetRequiredService<JourneyService>());
            await sp.GetRequiredService<IDbSession>().CommitAsync();
            return result;
        });

    private Task DispatchAsync() => world.Factory.Services.GetRequiredService<OutboxDispatcher>().DispatchBatchAsync(500, CancellationToken.None);

    private static async Task<Guid> JourneyOfAsync(TestTenant t, string user, string kind) =>
        (await OkAsync(t[user].Client.GetAsync("/api/v1/me/journeys"))).EnumerateArray().Single(j => j.GetProperty("kind").GetString() == kind).GetProperty("id").GetGuid();

    private static Task<JsonElement> JourneyAsync(TestTenant t, string user, Guid id) => OkAsync(t[user].Client.GetAsync($"{Journeys}/{id}"));

    private static JsonElement Step(JsonElement journey, string key) => journey.GetProperty("steps").EnumerateArray().Single(s => s.GetProperty("key").GetString() == key);

    private static async Task<JsonElement[]> InboxAsync(HttpClient client, string source) =>
        (await OkAsync(client.GetAsync("/api/v1/tasks"))).EnumerateArray().Where(i => i.GetProperty("source").GetString() == source).ToArray();

    private static object Clauses(string text) => new[]
    {
        new
        {
            level = 1, titles = Names(text, text + " ع"),
            bodies = new Dictionary<string, object>
            {
                ["en"] = new { blocks = new object[] { new { type = "paragraph", children = new[] { new { text = text + " text" } } } } },
                ["ar"] = new { blocks = new object[] { new { type = "paragraph", children = new[] { new { text = text + " نص" } } } } },
            },
        },
    };

    /// <summary>A published policy, flagged "onboarding required" through the register when asked.</summary>
    private static async Task<Guid> PolicyAsync(TestTenant t, string code, bool onboarding, Guid? unit = null)
    {
        var request = new Dictionary<string, object?>
        {
            ["code"] = code, ["names"] = Names(code, code + " ع"), ["orgUnitId"] = unit,
            ["route"] = new[] { new { approvers = new[] { t["legal"].Id } } },
        };
        var id = (await OkAsync(t["co"].Client.PostAsJsonAsync(Policies, request), HttpStatusCode.Created)).GetProperty("id").GetGuid();
        await OkAsync(t["co"].Client.PutAsJsonAsync($"{Policies}/{id}/draft", new { titles = Names(code, code + " ع"), clauses = Clauses(code), material = true }));
        await OkAsync(t["co"].Client.PostAsJsonAsync($"{Policies}/{id}/submit", new { }));
        var task = (await OkAsync(t["legal"].Client.GetAsync("/api/v1/tasks"))).EnumerateArray().Single(i => i.GetProperty("source").GetString() == "policy_approval");
        await OkAsync(t["legal"].Client.PostAsJsonAsync($"/api/v1/tasks/{task.GetProperty("taskId").GetGuid()}/decision", new { outcome = "approve" }));
        if (onboarding)
        {
            request["onboardingRequired"] = true;
            Assert.True((await OkAsync(t["co"].Client.PutAsJsonAsync($"{Policies}/{id}", request))).GetProperty("onboardingRequired").GetBoolean());
        }
        return id;
    }

    [Fact]
    public async Task Step_types_are_validated_against_the_assignee_and_the_kind()
    {
        var (t, _, _) = await FreshAsync();
        var invalid = await t["admin"].Client.PostAsJsonAsync(Templates, new
        {
            key = "onboarding", kind = "onboarding", names = Names("Onboarding", "التعيين"), startOn = "hire",
            steps = new object[]
            {
                new { key = "policies", names = Names("Accept the policies", "اقبل السياسات"), offsetDays = 0, assignee = "manager", type = "attestation" },
                new { key = "exit", names = Names("Exit interview", "مقابلة نهاية الخدمة"), offsetDays = 0, assignee = "person", type = "exit_interview" },
                new { key = "access", names = Names("Access", "الصلاحيات"), offsetDays = 0, assignee = "person", type = "access_removal" },
                new { key = "laptop", names = Names("Laptop", "الحاسوب"), offsetDays = 0, assignee = "users", users = new[] { t["karim"].Id }, systems = new[] { "VPN" } },
                new { key = "odd", names = Names("Odd", "غريب"), offsetDays = 0, assignee = "person", type = "unknown" },
            },
        });
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        Assert.Equal(["validation.journey.step_type", "validation.journey.systems"], (await ErrorKeysAsync(invalid)).Distinct().Order(StringComparer.Ordinal).ToArray());
        var duplicate = await t["admin"].Client.PostAsJsonAsync(Templates, new
        {
            key = "offboarding", kind = "offboarding", names = Names("Offboarding", "إنهاء الخدمة"), startOn = "termination",
            steps = new object[] { new { key = "access", names = Names("Access", "الصلاحيات"), offsetDays = 0, assignee = "permission", permission = Permissions.HrManage, type = "access_removal", systems = new[] { "VPN", "vpn" } } },
        });
        Assert.Contains("validation.journey.systems", await ErrorKeysAsync(duplicate));
    }

    [Fact]
    public async Task An_attestation_step_asks_for_the_onboarding_policies_and_is_done_when_the_person_has_attested()
    {
        var (t, _, egypt) = await FreshAsync();
        var conduct = await PolicyAsync(t, "COC", onboarding: true);
        var security = await PolicyAsync(t, "SEC", onboarding: true);
        await PolicyAsync(t, "TRAVEL", onboarding: false);
        await PolicyAsync(t, "EGYPT", onboarding: true, unit: egypt);
        await OkAsync(t["admin"].Client.PostAsJsonAsync(Templates, new
        {
            key = "onboarding", kind = "onboarding", names = Names("Onboarding", "التعيين"), startOn = "hire",
            steps = new object[]
            {
                new { key = "policies", names = Names("Accept the policies", "اقبل السياسات"), offsetDays = 0, assignee = "person", type = "attestation", dueWorkingDays = 3 },
                new { key = "welcome", names = Names("Read the welcome pack", "اقرأ دليل الترحيب"), offsetDays = 0, assignee = "person" },
            },
        }), HttpStatusCode.Created);
        var today = await TodayAsync(t);

        // Day one: the person owes the flagged policies that apply to them, not the unflagged one nor another unit's.
        await HandleAsync(t, new EmployeeHired(t["mona"].Id, "R1", today, Guid.NewGuid(), Guid.NewGuid()));
        var id = await JourneyOfAsync(t, "mona", "onboarding");
        var step = Step(await JourneyAsync(t, "mona", id), "policies");
        Assert.Equal(("attestation", "open", JsonValueKind.Null), (step.GetProperty("type").GetString(), step.GetProperty("status").GetString(), step.GetProperty("myTaskId").ValueKind));
        var owed = await InboxAsync(t["mona"].Client, "policy_attestation");
        Assert.Equal(2, owed.Length);
        Assert.All(owed, o => Assert.Equal(JsonValueKind.String, o.GetProperty("dueAt").ValueKind));
        Assert.Single(await InboxAsync(t["mona"].Client, "journey")); // The welcome step only: the policies show as attestations.
        var mine = (await OkAsync(t["mona"].Client.GetAsync($"{Policies}/{conduct}/attestation"))).GetProperty("due");
        Assert.Equal(JsonValueKind.Null, mine.GetProperty("campaignId").ValueKind);

        // Attesting to one leaves the step open; attesting to the last closes it with the person as the one who did it.
        await OkAsync(t["mona"].Client.PostAsJsonAsync($"{Policies}/{conduct}/attest", new { locale = "en" }), HttpStatusCode.Created);
        Assert.Contains("conflict.policies.nothing_to_attest", await ErrorKeysAsync(await t["mona"].Client.PostAsJsonAsync($"{Policies}/{conduct}/attest", new { })));
        await DispatchAsync();
        Assert.Equal("open", Step(await JourneyAsync(t, "mona", id), "policies").GetProperty("status").GetString());
        await OkAsync(t["mona"].Client.PostAsJsonAsync($"{Policies}/{security}/attest", new { }), HttpStatusCode.Created);
        await DispatchAsync();
        step = Step(await JourneyAsync(t, "hr_gulf", id), "policies");
        Assert.Equal(("done", "done", t["mona"].Id), (step.GetProperty("status").GetString(), step.GetProperty("outcome").GetString(),
            step.GetProperty("completedBy").GetProperty("id").GetGuid()));
        Assert.Empty(await InboxAsync(t["mona"].Client, "policy_attestation"));
        Assert.Equal(2L, await world.Database.TenantScalarAsync<long>(t.Id, "select count(*) from policies.attestation where campaign_id is null"));

        // A cancelled journey cancels what is still owed.
        await HandleAsync(t, new EmployeeHired(t["karim"].Id, "R1", today, Guid.NewGuid(), Guid.NewGuid()));
        var karims = await JourneyOfAsync(t, "karim", "onboarding");
        Assert.Equal(2, (await InboxAsync(t["karim"].Client, "policy_attestation")).Length);
        await OkAsync(t["hr_gulf"].Client.PostAsJsonAsync($"{Journeys}/{karims}/cancel", new { reason = "Offer withdrawn" }));
        Assert.Empty(await InboxAsync(t["karim"].Client, "policy_attestation"));

        // Nothing flagged: the step is not needed. The worker closes a step whose policies were retired meanwhile.
        await world.Database.OwnerExecInTenantAsync(t.Id, "update policies.policy set onboarding_required = false where tenant_id = @t", ("t", t.Id));
        await HandleAsync(t, new EmployeeHired(t["rana"].Id, "R1", today, Guid.NewGuid(), Guid.NewGuid()));
        step = Step(await JourneyAsync(t, "rana", await JourneyOfAsync(t, "rana", "onboarding")), "policies");
        Assert.Equal(("done", "skipped"), (step.GetProperty("status").GetString(), step.GetProperty("outcome").GetString()));
        Assert.Equal(0, await WorkAsync(t, j => j.SettleAttestationsAsync(null, default)));

        // Another tenant sees none of it.
        var other = await world.CreateTenantAsync("js-o-" + Guid.NewGuid().ToString("N")[..6], "workplace_plus");
        Assert.Equal(0L, await world.Database.TenantScalarAsync<long>(other.Id, "select count(*) from policies.obligation"));
        Assert.Equal(HttpStatusCode.NotFound, (await other["admin"].Client.GetAsync($"{Journeys}/{id}")).StatusCode);
    }

    [Fact]
    public async Task An_access_removal_checklist_lists_every_connected_system_is_confirmed_line_by_line_and_escalates_after_a_working_day()
    {
        var (t, _, _) = await FreshAsync();
        var connection = Guid.NewGuid();
        await world.Database.OwnerExecInTenantAsync(t.Id,
            """
            insert into integrations.connection (tenant_id, id, kind, display_name, status, enabled, rate_limit_per_minute)
            values (@t, @id, 'test_ticketing', 'Service desk', 'healthy', true, 600), (@t, gen_random_uuid(), 'test_ticketing', 'Old desk', 'disabled', true, 600)
            """, ("t", t.Id), ("id", connection));
        await OkAsync(t["admin"].Client.PostAsJsonAsync(Templates, new
        {
            key = "offboarding", kind = "offboarding", names = Names("Offboarding", "إنهاء الخدمة"), startOn = "termination",
            steps = new object[]
            {
                new
                {
                    key = "access", names = Names("Remove access", "إزالة الصلاحيات"), offsetDays = 0, assignee = "users", users = new[] { t["karim"].Id },
                    type = "access_removal", systems = new[] { "Payroll", "Door badges" },
                },
                new { key = "settlement", names = Names("Final settlement", "التسوية النهائية"), offsetDays = 5, assignee = "permission", permission = Permissions.HrManage },
            },
        }), HttpStatusCode.Created);
        var today = await TodayAsync(t);
        await HandleAsync(t, new EmployeeTerminated(t["mona"].Id, "R1", today, false, Guid.NewGuid(), Guid.NewGuid()));
        var id = await JourneyOfAsync(t, "mona", "offboarding");

        // One line per enabled connection and per system the template names; the leaver sees the step, not the lines.
        var step = Step(await JourneyAsync(t, "karim", id), "access");
        var items = step.GetProperty("accessItems").EnumerateArray().ToArray();
        Assert.Equal(["Service desk", "Payroll", "Door badges"], items.Select(i => i.GetProperty("name").GetString()!).ToArray());
        Assert.Equal(["connected", "listed", "listed"], items.Select(i => i.GetProperty("kind").GetString()!).ToArray());
        Assert.Equal(JsonValueKind.Null, Step(await JourneyAsync(t, "mona", id), "access").GetProperty("accessItems").ValueKind);
        Assert.Equal(3, Step(await JourneyAsync(t, "hr_gulf", id), "access").GetProperty("accessItems").GetArrayLength());

        // The step cannot be marked done from the inbox while lines are pending; only the task holder confirms lines.
        var task = Assert.Single(await InboxAsync(t["karim"].Client, "journey")).GetProperty("taskId").GetGuid();
        Assert.Contains("conflict.journey.checklist_pending", await ErrorKeysAsync(await t["karim"].Client.PostAsJsonAsync($"/api/v1/tasks/{task}/decision", new { outcome = "approve" })));
        Assert.Contains("conflict.journey.checklist_pending", await ErrorKeysAsync(await t["karim"].Client.PostAsJsonAsync($"/api/v1/tasks/{task}/decision",
            new { outcome = "reject", comment = "Not needed" })));
        var line = (Guid item) => $"{Journeys}/{id}/steps/access/access-items/{item}";
        var first = items[0].GetProperty("id").GetGuid();
        Assert.Contains("validation.journey.access_status", await ErrorKeysAsync(await t["karim"].Client.PostAsJsonAsync(line(first), new { status = "pending" })));
        Assert.Equal(HttpStatusCode.Forbidden, (await t["hr_gulf"].Client.PostAsJsonAsync(line(first), new { status = "removed" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await t["mona"].Client.PostAsJsonAsync(line(first), new { status = "removed" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await t["hr_egypt"].Client.PostAsJsonAsync(line(first), new { status = "removed" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await t["karim"].Client.PostAsJsonAsync(line(Guid.NewGuid()), new { status = "removed" })).StatusCode);
        var confirmed = await OkAsync(t["karim"].Client.PostAsJsonAsync(line(first), new { status = "removed", note = "Account closed" }));
        Assert.Equal(("removed", t["karim"].Id), (Step(confirmed, "access").GetProperty("accessItems")[0].GetProperty("status").GetString(),
            Step(confirmed, "access").GetProperty("accessItems")[0].GetProperty("confirmedBy").GetProperty("id").GetGuid()));
        Assert.Contains("conflict.journey.item_confirmed", await ErrorKeysAsync(await t["karim"].Client.PostAsJsonAsync(line(first), new { status = "removed" })));
        await world.Database.AssertAuditedAsync(t.Id, AuditActions.HrAccessRemovalConfirmed, id.ToString());
        await OkAsync(t["karim"].Client.PostAsJsonAsync(line(items[1].GetProperty("id").GetGuid()), new { status = "not_applicable" }));

        // Still within the first working day nothing is escalated; after it, the open line goes to IT and security, once.
        Assert.Equal(0, await WorkAsync(t, j => j.EscalateAccessAsync(default)));
        await world.Database.OwnerExecInTenantAsync(t.Id, "update hr.journey_step set opened_at = now() - interval '6 days' where tenant_id = @t and key = 'access'", ("t", t.Id));
        Assert.Equal(1, await WorkAsync(t, j => j.EscalateAccessAsync(default)));
        Assert.Equal(0, await WorkAsync(t, j => j.EscalateAccessAsync(default)));
        await world.Database.AssertAuditedAsync(t.Id, AuditActions.HrAccessRemovalEscalated, id.ToString());
        await DispatchAsync();
        Assert.True(await world.Database.CountInTenantAsync(t.Id, "select count(*) from notifications.notification where user_id = @u and template_key = @k",
            ("u", t["admin"].Id), ("k", JourneyService.AccessEscalatedTemplate)) >= 1);
        Assert.Equal(0L, await world.Database.CountInTenantAsync(t.Id, "select count(*) from notifications.notification where user_id = @u and template_key = @k",
            ("u", t["mona"].Id), ("k", JourneyService.AccessEscalatedTemplate)));
        var escalated = Step(await JourneyAsync(t, "karim", id), "access").GetProperty("accessItems")[2];
        Assert.Equal(JsonValueKind.String, escalated.GetProperty("escalatedAt").ValueKind);

        // The last line completes the task and the step.
        step = Step(await OkAsync(t["karim"].Client.PostAsJsonAsync(line(items[2].GetProperty("id").GetGuid()), new { status = "removed" })), "access");
        Assert.Equal(("done", "done"), (step.GetProperty("status").GetString(), step.GetProperty("outcome").GetString()));
        Assert.Empty(await InboxAsync(t["karim"].Client, "journey"));
        Assert.Contains("conflict.journey.step_closed", await ErrorKeysAsync(await t["karim"].Client.PostAsJsonAsync(line(first), new { status = "removed" })));

        // Another tenant reaches nothing.
        var other = await world.CreateTenantAsync("js-x-" + Guid.NewGuid().ToString("N")[..6], "workplace_plus");
        Assert.Equal(HttpStatusCode.NotFound, (await other["admin"].Client.PostAsJsonAsync(line(first), new { status = "removed" })).StatusCode);
    }

    [Fact]
    public async Task An_exit_interview_records_structured_reasons_counted_with_the_minimum_group_size_and_free_text_only_for_hr()
    {
        var leavers = new[] { "l1", "l2", "l3", "l4", "l5" };
        var (t, _, _) = await FreshAsync(leavers);
        await OkAsync(t["admin"].Client.PostAsJsonAsync(Templates, new
        {
            key = "offboarding", kind = "offboarding", names = Names("Offboarding", "إنهاء الخدمة"), startOn = "termination",
            steps = new object[]
            {
                new { key = "exit", names = Names("Exit interview", "مقابلة نهاية الخدمة"), offsetDays = 0, assignee = "person", type = "exit_interview" },
                new { key = "settlement", names = Names("Final settlement", "التسوية النهائية"), offsetDays = 5, assignee = "permission", permission = Permissions.HrManage },
            },
        }), HttpStatusCode.Created);
        var today = await TodayAsync(t);
        await HandleAsync(t, new EmployeeTerminated(t["mona"].Id, "R1", today, false, Guid.NewGuid(), Guid.NewGuid()));
        var id = await JourneyOfAsync(t, "mona", "offboarding");
        var url = $"{Journeys}/{id}/steps/exit/exit-interview";

        // The step is done through the form, not from the inbox; the answers are checked.
        var task = Assert.Single(await InboxAsync(t["mona"].Client, "journey")).GetProperty("taskId").GetGuid();
        Assert.Contains("conflict.journey.exit_pending", await ErrorKeysAsync(await t["mona"].Client.PostAsJsonAsync($"/api/v1/tasks/{task}/decision", new { outcome = "approve" })));
        Assert.Equal(["validation.journey.exit_primary", "validation.journey.exit_rating", "validation.journey.exit_reasons"],
            (await ErrorKeysAsync(await t["mona"].Client.PostAsJsonAsync(url, new { reasons = new[] { "career_growth", "boredom" }, primaryReason = "health", rating = 9 })))
                .Order(StringComparer.Ordinal).ToArray());
        Assert.Contains("validation.journey.exit_declined", await ErrorKeysAsync(await t["mona"].Client.PostAsJsonAsync(url, new { declined = true, comment = "No" })));
        // HR officers of the unit may hold the interview too (the step falls back to them); nobody else reaches it.
        Assert.Equal(JsonValueKind.String, Step(await JourneyAsync(t, "hr_gulf", id), "exit").GetProperty("myTaskId").ValueKind);
        Assert.Equal(HttpStatusCode.NotFound, (await t["karim"].Client.PostAsJsonAsync(url, new { reasons = new[] { "culture" } })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await t["hr_egypt"].Client.PostAsJsonAsync(url, new { reasons = new[] { "culture" } })).StatusCode);

        const string comment = "My manager never had time for one to ones";
        var view = await OkAsync(t["mona"].Client.PostAsJsonAsync(url, new
        {
            reasons = new[] { "career_growth", "management" }, primaryReason = "career_growth", rating = 4, wouldReturn = true, comment,
        }));
        Assert.Equal(("done", "done"), (Step(view, "exit").GetProperty("status").GetString(), Step(view, "exit").GetProperty("outcome").GetString()));
        Assert.Contains("conflict.journey.step_closed", await ErrorKeysAsync(await t["mona"].Client.PostAsJsonAsync(url, new { reasons = new[] { "culture" } })));

        // The free text is for HR officers of the unit; the leaver sees their structured answers; it never reaches the audit log.
        var hr = (await JourneyAsync(t, "hr_gulf", id)).GetProperty("exitInterview");
        Assert.Equal((comment, "career_growth", 4), (hr.GetProperty("comment").GetString(), hr.GetProperty("primaryReason").GetString(), hr.GetProperty("rating").GetInt32()));
        var own = (await JourneyAsync(t, "mona", id)).GetProperty("exitInterview");
        Assert.Equal((JsonValueKind.Null, 2), (own.GetProperty("comment").ValueKind, own.GetProperty("reasons").GetArrayLength()));
        Assert.Equal(HttpStatusCode.NotFound, (await t["hr_egypt"].Client.GetAsync($"{Journeys}/{id}")).StatusCode);
        await world.Database.AssertAuditedAsync(t.Id, AuditActions.HrExitInterviewRecorded, id.ToString());
        Assert.Equal(0L, await world.Database.CountInTenantAsync(t.Id, "select count(*) from audit.audit_event where changes::text like @c", ("c", "%one to ones%")));

        // Fewer than five answers: no counts. A declined interview counts as declined only.
        var summary = await OkAsync(t["hr_gulf"].Client.GetAsync("/api/v1/hr/exit-reasons"));
        Assert.Equal((1, 0, 1), (summary.GetProperty("interviews").GetInt32(), summary.GetProperty("reasons").GetArrayLength(), summary.GetProperty("hidden").GetInt32()));
        foreach (var leaver in leavers)
        {
            await HandleAsync(t, new EmployeeTerminated(t[leaver].Id, "R1", today, false, Guid.NewGuid(), Guid.NewGuid()));
            var journey = await JourneyOfAsync(t, leaver, "offboarding");
            var body = leaver == "l5" ? (object)new { declined = true } : new { reasons = new[] { "compensation", "career_growth" }, primaryReason = "compensation" };
            var recorded = await OkAsync(t[leaver].Client.PostAsJsonAsync($"{Journeys}/{journey}/steps/exit/exit-interview", body));
            Assert.Equal(leaver == "l5" ? "skipped" : "done", Step(recorded, "exit").GetProperty("outcome").GetString());
        }
        summary = await OkAsync(t["hr_gulf"].Client.GetAsync("/api/v1/hr/exit-reasons"));
        Assert.Equal((6, 1, 0), (summary.GetProperty("interviews").GetInt32(), summary.GetProperty("declined").GetInt32(), summary.GetProperty("hidden").GetInt32()));
        var reasons = summary.GetProperty("reasons").EnumerateArray().ToDictionary(r => r.GetProperty("reason").GetString()!, r => (r.GetProperty("chosen").GetInt32(), r.GetProperty("primary").GetInt32()));
        Assert.Equal((5, 1), reasons["career_growth"]);
        Assert.Equal((4, 4), reasons["compensation"]);
        Assert.Equal(5, Assert.Single(summary.GetProperty("units").EnumerateArray()).GetProperty("interviews").GetInt32());
        Assert.DoesNotContain(comment, summary.GetRawText(), StringComparison.Ordinal);

        // Other units, people without hr.manage, other tenants and plans without journeys get nothing.
        Assert.Equal(0, (await OkAsync(t["hr_egypt"].Client.GetAsync("/api/v1/hr/exit-reasons"))).GetProperty("interviews").GetInt32());
        Assert.Equal(HttpStatusCode.Forbidden, (await t["mona"].Client.GetAsync("/api/v1/hr/exit-reasons")).StatusCode);
        Assert.Contains("validation.journey.period", await ErrorKeysAsync(await t["hr_gulf"].Client.GetAsync("/api/v1/hr/exit-reasons?from=2026-05-01&to=2026-01-01")));
        var other = await world.CreateTenantAsync("js-e-" + Guid.NewGuid().ToString("N")[..6], "workplace_plus");
        Assert.Equal(0, (await OkAsync(other["admin"].Client.GetAsync("/api/v1/hr/exit-reasons"))).GetProperty("interviews").GetInt32());
        var basic = await world.CreateTenantAsync("js-b-" + Guid.NewGuid().ToString("N")[..6], "workplace");
        Assert.Equal(HttpStatusCode.Forbidden, (await basic["admin"].Client.GetAsync("/api/v1/hr/exit-reasons")).StatusCode);
    }
}
