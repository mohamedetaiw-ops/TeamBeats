using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TeamBeats.Audit.Contracts;
using TeamBeats.Authorization.Contracts;
using TeamBeats.Modules.Policies;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Persistence;
using TeamBeats.TenantContext;
using TeamBeats.Testing;
using Xunit;

namespace TeamBeats.Integration.Tests;

/// <summary>
/// Spec 025 T003 and T004 (US1, FR-001 to FR-005): the register; drafts with numbered clauses in Arabic and English;
/// the sequential route in the inbox with compare against the version in force and clause comments; numbers, effective
/// dates and the superseded version on the date; the binding language and variants approved together; the superseded
/// banner; no self-approval by the owner, editors or submitter; unit policies kept from sibling units; scoped
/// compliance officers; other tenants get not found.
/// </summary>
[Collection(WorldCollection.Name)]
public sealed class PolicyApprovalTests(TenantWorld world)
{
    private const string Policies = "/api/v1/policies";

    private sealed record Org(TestTenant T, Guid Gulf, Guid Egypt);

    private async Task<Org> FreshAsync()
    {
        var t = await world.CreateTenantAsync("pol-" + Guid.NewGuid().ToString("N")[..8], "workplace_plus", "en", ["en", "ar"]);
        var holding = await world.CreateUnitAsync(t, "Holding", null, "organization");
        var gulf = await world.CreateUnitAsync(t, "Gulf", holding, "subsidiary");
        var egypt = await world.CreateUnitAsync(t, "Egypt", holding, "subsidiary");
        foreach (var name in new[] { "co", "co2", "egco", "sara", "legal", "ciso", "ceo", "emp", "cai" })
        {
            await world.SeedUserAsync(t, name, MembershipTypes.Employee);
        }
        foreach (var name in new[] { "co", "co2", "sara", "legal", "ciso", "ceo", "emp" })
        {
            await world.AddMembershipAsync(t, name, "Gulf");
        }
        await world.AddMembershipAsync(t, "egco", "Egypt");
        await world.AddMembershipAsync(t, "cai", "Egypt");
        await world.AssignAsync(t, "co", BuiltInRoles.ComplianceOfficer, "tenant", null);
        await world.AssignAsync(t, "co2", BuiltInRoles.ComplianceOfficer, "tenant", null);
        await world.AssignAsync(t, "egco", BuiltInRoles.ComplianceOfficer, "org_subtree", egypt);
        var role = await t["admin"].Client.PostAsJsonAsync("/api/v1/authz/roles", new
        {
            key = "policy_writer", names = new Dictionary<string, string> { ["en"] = "Policy writer" },
            permissions = new[] { Permissions.PolicyAuthor }, delegable = true,
        });
        Assert.Equal(HttpStatusCode.Created, role.StatusCode);
        await world.AssignAsync(t, "sara", "policy_writer", "tenant", null);
        return new Org(t, gulf, egypt);
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

    private static object Body(string text) => new { blocks = new object[] { new { type = "paragraph", children = new[] { new { text } } } } };

    private static object Clause(string? key, int level, string en, string? ar) => new
    {
        key, level,
        titles = ar is null ? new Dictionary<string, string> { ["en"] = en } : new Dictionary<string, string> { ["en"] = en, ["ar"] = ar },
        bodies = ar is null ? new Dictionary<string, object> { ["en"] = Body(en + " text") } : new Dictionary<string, object> { ["en"] = Body(en + " text"), ["ar"] = Body(ar + " نص") },
    };

    private static object Titles(bool arabic = true) =>
        arabic ? new Dictionary<string, string> { ["en"] = "Acceptable use", ["ar"] = "الاستخدام المقبول" } : new Dictionary<string, string> { ["en"] = "Acceptable use" };

    private static Task<HttpResponseMessage> CreateAsync(Org o, string asUser, object? route = null, Guid? unit = null, string? binding = "ar", string? owner = "sara", string code = "POL-001") =>
        o.T[asUser].Client.PostAsJsonAsync(Policies, new
        {
            code, names = new Dictionary<string, string> { ["en"] = "Acceptable use", ["ar"] = "الاستخدام المقبول" },
            ownerId = owner is null ? (Guid?)null : o.T[owner].Id, orgUnitId = unit, bindingLocale = binding, classification = "internal", route,
        });

    private static async Task<JsonElement[]> InboxAsync(HttpClient client) =>
        (await OkAsync(client.GetAsync("/api/v1/tasks"))).EnumerateArray().Where(t => t.GetProperty("source").GetString() == "policy_approval").ToArray();

    private static async Task DecideAsync(HttpClient client, string outcome, string? comment = null)
    {
        var task = Assert.Single(await InboxAsync(client)).GetProperty("taskId").GetGuid();
        await OkAsync(client.PostAsJsonAsync($"/api/v1/tasks/{task}/decision", new { outcome, comment }));
    }

    private Task<int> RunDueAsync(TestTenant t, DateTimeOffset now) =>
        world.Factory.InScopeAsync(t.Id, async sp =>
        {
            sp.GetRequiredService<TenantContextAccessor>().SetActor(ActorType.System, null);
            var moved = await sp.GetRequiredService<PolicyApprovals>().RunDueAsync(now, 100, CancellationToken.None);
            await sp.GetRequiredService<IDbSession>().CommitAsync();
            return moved;
        });

    private static string[] Changes(JsonElement compare) =>
        compare.GetProperty("clauses").EnumerateArray().Select(c => c.GetProperty("change").GetString()!).ToArray();

    [Fact]
    public async Task A_version_runs_its_route_with_compare_and_comments_then_the_next_takes_effect_on_its_date_and_supersedes_the_last()
    {
        var o = await FreshAsync();
        var t = o.T;
        var route = new object[]
        {
            new { names = new Dictionary<string, string> { ["en"] = "Legal", ["ar"] = "القانونية" }, approvers = new[] { t["legal"].Id } },
            new { names = new Dictionary<string, string> { ["en"] = "CISO" }, approvers = new[] { t["ciso"].Id } },
            new { names = new Dictionary<string, string> { ["en"] = "CEO" }, approvers = new[] { t["ceo"].Id } },
        };
        var policy = Id(await OkAsync(CreateAsync(o, "co", route), HttpStatusCode.Created));
        await world.Database.AssertAuditedAsync(t.Id, AuditActions.PolicyCreated, policy.ToString());

        // The owner drafts in both languages; the draft stays hidden from the people it will apply to.
        var draft = await OkAsync(t["sara"].Client.PutAsJsonAsync($"{Policies}/{policy}/draft", new
        {
            titles = Titles(), clauses = new[] { Clause(null, 1, "Scope", "النطاق"), Clause(null, 2, "Devices", "الأجهزة"), Clause(null, 1, "Gifts", "الهدايا") },
            material = true,
        }));
        await world.Database.AssertAuditedAsync(t.Id, AuditActions.PolicyDraftSaved, policy.ToString());
        var clauses = draft.GetProperty("clauses").EnumerateArray().ToArray();
        Assert.Equal(["1", "1.1", "2"], clauses.Select(c => c.GetProperty("number").GetString()));
        Assert.All(clauses, c => Assert.Equal("clause-" + c.GetProperty("key").GetString(), c.GetProperty("anchor").GetString()));
        var keys = clauses.Select(c => c.GetProperty("key").GetString()!).ToArray();
        Assert.Equal(HttpStatusCode.NotFound, (await t["emp"].Client.GetAsync($"{Policies}/{policy}")).StatusCode);

        await OkAsync(t["sara"].Client.PostAsJsonAsync($"{Policies}/{policy}/submit", new { }));
        await world.Database.AssertAuditedAsync(t.Id, AuditActions.PolicySubmitted, policy.ToString());
        Assert.Equal("in_review", (await OkAsync(t["co"].Client.GetAsync($"{Policies}/{policy}"))).GetProperty("status").GetString());
        // In review, the draft cannot change.
        Assert.Contains("conflict.policies.in_review", await ErrorKeysAsync(await t["sara"].Client.PutAsJsonAsync($"{Policies}/{policy}/draft",
            new { titles = Titles(), clauses = Array.Empty<object>() })));

        // Only the first step's approver has the task; the first version compares against nothing in force.
        Assert.Empty(await InboxAsync(t["ciso"].Client));
        var legalTask = Assert.Single(await InboxAsync(t["legal"].Client));
        Assert.Equal($"/policies/{policy}/review", legalTask.GetProperty("link").GetString());
        var first = await OkAsync(t["legal"].Client.GetAsync($"{Policies}/{policy}/compare"));
        Assert.Equal(JsonValueKind.Null, first.GetProperty("base").ValueKind);
        Assert.Equal(["added", "added", "added"], Changes(first));
        await DecideAsync(t["legal"].Client, "approve");
        await DecideAsync(t["ciso"].Client, "approve");
        await DecideAsync(t["ceo"].Client, "approve");
        await world.Database.AssertAuditedAsync(t.Id, AuditActions.PolicyApproved, policy.ToString());
        await world.Database.AssertAuditedAsync(t.Id, AuditActions.PolicyPublished, policy.ToString());

        // Approved without a date: number 1 is in force at once, with the approval trail; the reader sees the binding language.
        var view = await OkAsync(t["co"].Client.GetAsync($"{Policies}/{policy}"));
        Assert.Equal("published", view.GetProperty("status").GetString());
        Assert.Equal(1, view.GetProperty("currentVersion").GetInt32());
        Assert.Equal(JsonValueKind.Null, view.GetProperty("working").ValueKind);
        var current = await OkAsync(t["emp"].Client.GetAsync($"{Policies}/{policy}/current"));
        Assert.Equal("ar", current.GetProperty("bindingLocale").GetString());
        Assert.False(current.GetProperty("superseded").GetBoolean());
        var approvals = current.GetProperty("version").GetProperty("approvals").EnumerateArray().ToArray();
        Assert.Equal([t["legal"].Id, t["ciso"].Id, t["ceo"].Id], approvals.Select(a => a.GetProperty("approver").GetProperty("id").GetGuid()));
        Assert.Equal("القانونية", approvals[0].GetProperty("stepNames").GetProperty("ar").GetString());
        Assert.Contains(policy, (await OkAsync(t["emp"].Client.GetAsync(Policies))).EnumerateArray().Select(Id));

        // Version 2 keeps clause 1, changes clause 2, drops the gifts clause and adds one; it takes effect in three days.
        await OkAsync(t["sara"].Client.PutAsJsonAsync($"{Policies}/{policy}/draft", new
        {
            titles = Titles(),
            clauses = new[] { Clause(keys[0], 1, "Scope", "النطاق"), Clause(keys[1], 2, "Personal devices", "الأجهزة الشخصية"), Clause(null, 1, "Email", "البريد") },
            material = false, changeNote = "Personal devices",
        }));
        var effectiveOn = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(3));
        await OkAsync(t["sara"].Client.PostAsJsonAsync($"{Policies}/{policy}/submit", new { effectiveOn }));
        var compare = await OkAsync(t["legal"].Client.GetAsync($"{Policies}/{policy}/compare"));
        Assert.Equal(1, compare.GetProperty("base").GetInt32());
        Assert.Equal(["unchanged", "changed", "added", "removed"], Changes(compare));

        // Legal comments on the changed clause and returns it; the owner sees the comment by that clause.
        var versionId = compare.GetProperty("versionId").GetGuid();
        var comment = await OkAsync(t["legal"].Client.PostAsJsonAsync($"{Policies}/{policy}/drafts/{versionId}/comments",
            new { clauseKey = keys[1], text = "Say which devices." }), HttpStatusCode.Created);
        Assert.Contains("validation.policies.clause_key", await ErrorKeysAsync(await t["legal"].Client.PostAsJsonAsync($"{Policies}/{policy}/drafts/{versionId}/comments",
            new { clauseKey = "nosuchkey", text = "?" })));
        Assert.Equal(HttpStatusCode.NotFound, (await t["emp"].Client.GetAsync($"{Policies}/{policy}/drafts/{versionId}/comments")).StatusCode);
        await DecideAsync(t["legal"].Client, "return", "See my comment.");
        await world.Database.AssertAuditedAsync(t.Id, AuditActions.PolicyReturned, policy.ToString());
        var seen = Assert.Single((await OkAsync(t["sara"].Client.GetAsync($"{Policies}/{policy}/drafts/{versionId}/comments"))).EnumerateArray());
        Assert.Equal(keys[1], seen.GetProperty("clauseKey").GetString());
        Assert.Equal(t["legal"].Id, seen.GetProperty("author").GetProperty("id").GetGuid());
        Assert.Equal("draft", (await OkAsync(t["sara"].Client.GetAsync($"{Policies}/{policy}/draft"))).GetProperty("state").GetString());
        var resolved = await OkAsync(t["sara"].Client.PostAsync($"{Policies}/{policy}/drafts/{versionId}/comments/{Id(comment)}/resolve", null));
        Assert.Equal(t["sara"].Id, resolved.GetProperty("resolvedBy").GetProperty("id").GetGuid());
        Assert.Equal(HttpStatusCode.Forbidden, (await t["legal"].Client.PostAsync($"{Policies}/{policy}/drafts/{versionId}/comments/{Id(comment)}/resolve", null)).StatusCode);

        // Resubmitted and approved: number 2 waits for its date while number 1 stays in force.
        await OkAsync(t["sara"].Client.PostAsJsonAsync($"{Policies}/{policy}/submit", new { effectiveOn }));
        await DecideAsync(t["legal"].Client, "approve");
        await DecideAsync(t["ciso"].Client, "approve");
        await DecideAsync(t["ceo"].Client, "approve");
        view = await OkAsync(t["sara"].Client.GetAsync($"{Policies}/{policy}"));
        Assert.Equal(1, view.GetProperty("currentVersion").GetInt32());
        Assert.Equal(2, view.GetProperty("scheduled").GetProperty("number").GetInt32());
        Assert.Equal(HttpStatusCode.NotFound, (await t["emp"].Client.GetAsync($"{Policies}/{policy}/versions/2")).StatusCode);
        Assert.Equal(0, await RunDueAsync(t, DateTimeOffset.UtcNow));

        // On the date it comes into force and number 1 is superseded; an old link shows the banner's details.
        Assert.Equal(1, await RunDueAsync(t, DateTimeOffset.UtcNow.AddDays(4)));
        await world.Database.AssertAuditedAsync(t.Id, AuditActions.PolicySuperseded, policy.ToString());
        current = await OkAsync(t["emp"].Client.GetAsync($"{Policies}/{policy}/current"));
        Assert.Equal(2, current.GetProperty("version").GetProperty("number").GetInt32());
        Assert.False(current.GetProperty("version").GetProperty("material").GetBoolean());
        Assert.Equal(keys[0], current.GetProperty("version").GetProperty("clauses")[0].GetProperty("key").GetString());
        var old = await OkAsync(t["emp"].Client.GetAsync($"{Policies}/{policy}/versions/1"));
        Assert.True(old.GetProperty("superseded").GetBoolean());
        Assert.Equal(2, old.GetProperty("currentVersion").GetInt32());
        var history = await OkAsync(t["sara"].Client.GetAsync($"{Policies}/{policy}/versions"));
        Assert.Equal(["effective", "superseded"], history.EnumerateArray().Select(v => v.GetProperty("state").GetString()));
        Assert.Equal(HttpStatusCode.Forbidden, (await t["emp"].Client.GetAsync($"{Policies}/{policy}/versions")).StatusCode);
    }

    [Fact]
    public async Task Owners_editors_and_submitters_never_approve_and_every_language_is_approved_together()
    {
        var o = await FreshAsync();
        var t = o.T;
        // The owner is named on the route too, and the compliance officers are the second step.
        var route = new object[] { new { approvers = new[] { t["sara"].Id, t["legal"].Id } }, new { complianceOfficers = true } };
        var policy = Id(await OkAsync(CreateAsync(o, "co", route), HttpStatusCode.Created));

        // An English only draft cannot go while Arabic is the binding language; a clause missing its Arabic text cannot either.
        await OkAsync(t["sara"].Client.PutAsJsonAsync($"{Policies}/{policy}/draft", new { titles = Titles(arabic: false), clauses = new[] { Clause(null, 1, "Scope", null) } }));
        Assert.Contains("validation.policies.binding_locale_missing", await ErrorKeysAsync(await t["sara"].Client.PostAsJsonAsync($"{Policies}/{policy}/submit", new { })));
        await OkAsync(t["sara"].Client.PutAsJsonAsync($"{Policies}/{policy}/draft", new
        {
            titles = Titles(), clauses = new[] { Clause(null, 1, "Scope", "النطاق"), Clause(null, 1, "Gifts", null) },
        }));
        Assert.Contains("validation.policies.clause_incomplete", await ErrorKeysAsync(await t["sara"].Client.PostAsJsonAsync($"{Policies}/{policy}/submit", new { })));
        Assert.Contains("validation.policies.clause_level", await ErrorKeysAsync(await t["sara"].Client.PutAsJsonAsync($"{Policies}/{policy}/draft", new
        {
            titles = Titles(), clauses = new[] { Clause(null, 2, "Scope", "النطاق") },
        })));
        Assert.Contains("validation.policies.clause_block", await ErrorKeysAsync(await t["sara"].Client.PutAsJsonAsync($"{Policies}/{policy}/draft", new
        {
            titles = Titles(),
            clauses = new[] { new { level = 1, titles = new { en = "Scope" }, bodies = new { en = new { blocks = new[] { new { type = "embed", url = "https://example.com" } } } } } },
        })));
        Assert.Contains("validation.policies.effective_on", await ErrorKeysAsync(await t["sara"].Client.PostAsJsonAsync($"{Policies}/{policy}/submit",
            new { effectiveOn = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-3)) })));

        // A compliance officer edits the draft too, so they become an editor; then it goes complete in both languages.
        await OkAsync(t["co"].Client.PutAsJsonAsync($"{Policies}/{policy}/draft", new
        {
            titles = Titles(), clauses = new[] { Clause(null, 1, "Scope", "النطاق"), Clause(null, 1, "Gifts", "الهدايا") },
        }));
        await OkAsync(t["sara"].Client.PostAsJsonAsync($"{Policies}/{policy}/submit", new { }));
        Assert.Empty(await InboxAsync(t["sara"].Client));
        await DecideAsync(t["legal"].Client, "approve");
        // Step two: the editing compliance officer never gets it; the other one does.
        Assert.Empty(await InboxAsync(t["co"].Client));
        Assert.Single(await InboxAsync(t["co2"].Client));

        // The submitter withdraws; the version is a draft again and the task is gone.
        await OkAsync(t["sara"].Client.PostAsync($"{Policies}/{policy}/withdraw", null));
        await world.Database.AssertAuditedAsync(t.Id, AuditActions.PolicyWithdrawn, policy.ToString());
        Assert.Empty(await InboxAsync(t["co2"].Client));
        Assert.Equal("draft", (await OkAsync(t["co"].Client.GetAsync($"{Policies}/{policy}"))).GetProperty("status").GetString());
        Assert.Contains("conflict.policies.not_in_review", await ErrorKeysAsync(await t["sara"].Client.PostAsync($"{Policies}/{policy}/withdraw", null)));

        // Retired: no more drafts.
        await OkAsync(t["co"].Client.PostAsync($"{Policies}/{policy}/retire", null));
        await world.Database.AssertAuditedAsync(t.Id, AuditActions.PolicyRetired, policy.ToString());
        Assert.Contains("conflict.policies.retired", await ErrorKeysAsync(await t["sara"].Client.PutAsJsonAsync($"{Policies}/{policy}/draft",
            new { titles = Titles(), clauses = Array.Empty<object>() })));
    }

    [Fact]
    public async Task The_register_checks_its_entries_and_only_compliance_officers_change_them()
    {
        var o = await FreshAsync();
        var t = o.T;
        // Only compliance officers create entries; the owner must be able to author policies there.
        Assert.Equal(HttpStatusCode.Forbidden, (await CreateAsync(o, "sara")).StatusCode);
        Assert.Contains("validation.policies.owner", await ErrorKeysAsync(await CreateAsync(o, "co", owner: "emp")));
        var keys = await ErrorKeysAsync(await t["co"].Client.PostAsJsonAsync(Policies, new
        {
            code = "", names = new Dictionary<string, string> { ["fr"] = "Politique" }, bindingLocale = "fr", classification = "secret", reviewMonths = 0,
            route = new[] { new { complianceOfficers = true, approvers = new[] { t["legal"].Id } } }, related = new[] { Guid.NewGuid() },
        }));
        Assert.Contains("validation.policies.code", keys);
        Assert.Contains("validation.policies.locale", keys);
        Assert.Contains("validation.policies.binding_locale", keys);
        Assert.Contains("validation.policies.classification", keys);
        Assert.Contains("validation.policies.review_months", keys);
        Assert.Contains("validation.policies.route", keys);
        Assert.Contains("validation.policies.related", keys);

        var category = Id(await OkAsync(t["co"].Client.PostAsJsonAsync($"{Policies}/categories",
            new { names = new Dictionary<string, string> { ["en"] = "Security", ["ar"] = "الأمن" } }), HttpStatusCode.Created));
        Assert.Equal(HttpStatusCode.Forbidden, (await t["sara"].Client.PostAsJsonAsync($"{Policies}/categories",
            new { names = new Dictionary<string, string> { ["en"] = "Mine" } })).StatusCode);
        var policy = await OkAsync(CreateAsync(o, "co"), HttpStatusCode.Created);
        Assert.Contains("validation.policies.code_taken", await ErrorKeysAsync(await CreateAsync(o, "co", code: "pol-001")));
        Assert.True(policy.GetProperty("rights").GetProperty("manage").GetBoolean());

        // The owner edits drafts but not the register entry; the compliance officer changes it, audited.
        var id = Id(policy);
        var owner = await OkAsync(t["sara"].Client.GetAsync($"{Policies}/{id}"));
        Assert.False(owner.GetProperty("rights").GetProperty("manage").GetBoolean());
        Assert.True(owner.GetProperty("rights").GetProperty("edit").GetBoolean());
        Assert.Empty(owner.GetProperty("route").EnumerateArray());
        var change = new
        {
            code = "POL-001", names = new Dictionary<string, string> { ["en"] = "Acceptable use of IT" }, categoryId = category, ownerId = t["sara"].Id,
            classification = "confidential", reviewMonths = 24,
        };
        Assert.Equal(HttpStatusCode.Forbidden, (await t["sara"].Client.PutAsJsonAsync($"{Policies}/{id}", change)).StatusCode);
        var changed = await OkAsync(t["co"].Client.PutAsJsonAsync($"{Policies}/{id}", change));
        Assert.Equal(category, changed.GetProperty("categoryId").GetGuid());
        Assert.Equal(24, changed.GetProperty("reviewMonths").GetInt32());
        await world.Database.AssertAuditedAsync(t.Id, AuditActions.PolicyChanged, id.ToString());
        Assert.Single((await OkAsync(t["co"].Client.GetAsync($"{Policies}?category={category}"))).EnumerateArray());
        Assert.Single((await OkAsync(t["co"].Client.GetAsync($"{Policies}?q=it"))).EnumerateArray());
    }

    [Fact]
    public async Task A_unit_policy_stays_inside_its_unit_and_scoped_compliance_officers_and_other_tenants_reach_nothing_else()
    {
        var o = await FreshAsync();
        var t = o.T;
        // A Gulf policy with no audience applies to the Gulf subtree only.
        var gulf = Id(await OkAsync(CreateAsync(o, "co", new object[] { new { approvers = new[] { t["legal"].Id } } }, o.Gulf, binding: null), HttpStatusCode.Created));
        await OkAsync(t["sara"].Client.PutAsJsonAsync($"{Policies}/{gulf}/draft", new { titles = Titles(), clauses = new[] { Clause(null, 1, "Scope", "النطاق") } }));
        await OkAsync(t["sara"].Client.PostAsJsonAsync($"{Policies}/{gulf}/submit", new { }));
        await DecideAsync(t["legal"].Client, "approve");
        await OkAsync(t["emp"].Client.GetAsync($"{Policies}/{gulf}/current"));
        Assert.Equal(HttpStatusCode.NotFound, (await t["cai"].Client.GetAsync($"{Policies}/{gulf}/current")).StatusCode);
        Assert.DoesNotContain(gulf, (await OkAsync(t["cai"].Client.GetAsync(Policies))).EnumerateArray().Select(Id));

        // The Egypt compliance officer cannot create a Gulf policy and does not see Gulf entries; they manage Egypt's own.
        Assert.Equal(HttpStatusCode.Forbidden, (await CreateAsync(o, "egco", unit: o.Gulf, owner: null, code: "EG-X")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await t["egco"].Client.GetAsync($"{Policies}/{gulf}")).StatusCode);
        var egypt = Id(await OkAsync(CreateAsync(o, "egco", unit: o.Egypt, owner: "egco", code: "EG-1", binding: null), HttpStatusCode.Created));
        var seen = (await OkAsync(t["egco"].Client.GetAsync(Policies))).EnumerateArray().Select(Id).ToArray();
        Assert.Contains(egypt, seen);
        Assert.DoesNotContain(gulf, seen);
        Assert.Equal(HttpStatusCode.Forbidden, (await t["egco"].Client.PutAsJsonAsync($"{Policies}/{egypt}", new
        {
            code = "EG-1", names = new Dictionary<string, string> { ["en"] = "Moved" }, orgUnitId = o.Gulf,
        })).StatusCode);

        // Another tenant's IDs are not found, whatever the call.
        var other = await world.CreateTenantAsync("pol-x-" + Guid.NewGuid().ToString("N")[..6], "workplace_plus");
        var stranger = other["admin"].Client;
        Assert.Equal(HttpStatusCode.NotFound, (await stranger.GetAsync($"{Policies}/{gulf}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await stranger.GetAsync($"{Policies}/{gulf}/current")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await stranger.GetAsync($"{Policies}/{gulf}/versions/1")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await stranger.GetAsync($"{Policies}/{gulf}/compare")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await stranger.PutAsJsonAsync($"{Policies}/{gulf}/draft", new { titles = Titles(arabic: false) })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await stranger.PostAsJsonAsync($"{Policies}/{gulf}/submit", new { })).StatusCode);
        Assert.DoesNotContain(gulf, (await OkAsync(stranger.GetAsync(Policies))).EnumerateArray().Select(Id));
    }
}
