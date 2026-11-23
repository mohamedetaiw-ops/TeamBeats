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
/// Spec 025 T013 (US5, FR-012): the review window opens a review task for the owner; no change records the reviewer
/// and moves the review date with no attestation; a revision starts a draft for the route and, once a major revision
/// is in force, asks the owner about re-attestation; retire retires for a compliance officer and asks them otherwise;
/// a passed review date is overdue in the register with notices to the compliance officers over the policy's unit and
/// the owner's manager; employees and other tenants reach no review.
/// </summary>
[Collection(WorldCollection.Name)]
public sealed class PolicyReviewTests(TenantWorld world)
{
    private const string Policies = "/api/v1/policies";

    private sealed record Org(TestTenant T, Guid Gulf, Guid Egypt);

    private async Task<Org> FreshAsync()
    {
        var t = await world.CreateTenantAsync("prv-" + Guid.NewGuid().ToString("N")[..8], "workplace_plus", "en", ["en", "ar"]);
        var holding = await world.CreateUnitAsync(t, "Holding", null, "organization");
        var gulf = await world.CreateUnitAsync(t, "Gulf", holding, "subsidiary");
        var egypt = await world.CreateUnitAsync(t, "Egypt", holding, "subsidiary");
        foreach (var name in new[] { "co", "gco", "egco", "sara", "legal", "mona", "emp" })
        {
            await world.SeedUserAsync(t, name, MembershipTypes.Employee);
            await world.AddMembershipAsync(t, name, name == "egco" ? "Egypt" : "Gulf");
        }
        await world.AssignAsync(t, "co", BuiltInRoles.ComplianceOfficer, "tenant", null);
        await world.AssignAsync(t, "gco", BuiltInRoles.ComplianceOfficer, "org_subtree", holding);
        await world.AssignAsync(t, "egco", BuiltInRoles.ComplianceOfficer, "org_subtree", egypt);
        var role = await t["admin"].Client.PostAsJsonAsync("/api/v1/authz/roles", new
        {
            key = "policy_writer", names = new Dictionary<string, string> { ["en"] = "Policy writer" }, permissions = new[] { Permissions.PolicyAuthor }, delegable = true,
        });
        Assert.Equal(HttpStatusCode.Created, role.StatusCode);
        await world.AssignAsync(t, "sara", "policy_writer", "tenant", null);
        Assert.Equal(HttpStatusCode.OK, (await t["admin"].Client.PutAsJsonAsync($"/api/v1/people/{t["sara"].Id}/manager", new { managerId = t["mona"].Id })).StatusCode);
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

    private static object Clauses(string text) => new[]
    {
        new
        {
            level = 1, titles = new Dictionary<string, string> { ["en"] = text, ["ar"] = text + " ع" },
            bodies = new Dictionary<string, object>
            {
                ["en"] = new { blocks = new object[] { new { type = "paragraph", children = new[] { new { text = text + " text" } } } } },
                ["ar"] = new { blocks = new object[] { new { type = "paragraph", children = new[] { new { text = text + " نص" } } } } },
            },
        },
    };

    private static readonly Dictionary<string, string> Titles = new() { ["en"] = "Travel policy", ["ar"] = "سياسة السفر" };

    /// <summary>A Gulf policy owned by Sara with one approval step by legal, version 1 in force today.</summary>
    private async Task<Guid> PublishedAsync(Org o, string code, int? window = null)
    {
        var t = o.T;
        var id = Id(await OkAsync(t["co"].Client.PostAsJsonAsync(Policies, new
        {
            code, names = Titles, ownerId = t["sara"].Id, orgUnitId = o.Gulf, reviewWindowDays = window, route = new[] { new { approvers = new[] { t["legal"].Id } } },
        }), HttpStatusCode.Created));
        await OkAsync(t["sara"].Client.PutAsJsonAsync($"{Policies}/{id}/draft", new { titles = Titles, clauses = Clauses("Travel"), material = true }));
        await SubmitAndApproveAsync(o, id);
        return id;
    }

    private async Task SubmitAndApproveAsync(Org o, Guid id)
    {
        var t = o.T;
        await OkAsync(t["sara"].Client.PostAsJsonAsync($"{Policies}/{id}/submit", new { }));
        var task = (await OkAsync(t["legal"].Client.GetAsync("/api/v1/tasks"))).EnumerateArray().Single(i => i.GetProperty("source").GetString() == "policy_approval");
        await OkAsync(t["legal"].Client.PostAsJsonAsync($"/api/v1/tasks/{task.GetProperty("taskId").GetGuid()}/decision", new { outcome = "approve" }));
    }

    /// <summary>Puts the review date where the test needs it, as if the version took effect long ago.</summary>
    private Task SetReviewDateAsync(TestTenant t, Guid policy, DateTimeOffset at) =>
        world.Database.OwnerExecInTenantAsync(t.Id, "update policies.policy set next_review_at = @at where id = @id", ("at", at), ("id", policy));

    private Task<int> RunDueAsync(TestTenant t, DateTimeOffset now) =>
        world.Factory.InScopeAsync(t.Id, async sp =>
        {
            sp.GetRequiredService<TenantContextAccessor>().SetActor(ActorType.System, null);
            var moved = await sp.GetRequiredService<PolicyReviews>().RunDueAsync(now, 200, CancellationToken.None);
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

    private static async Task<JsonElement[]> ReviewTasksAsync(HttpClient client) =>
        (await OkAsync(client.GetAsync("/api/v1/tasks"))).EnumerateArray().Where(t => t.GetProperty("source").GetString() == "policy_review").ToArray();

    private static async Task<string?> RegisterStateAsync(HttpClient client, Guid policy)
    {
        var row = (await OkAsync(client.GetAsync(Policies))).EnumerateArray().Single(p => Id(p) == policy);
        return row.TryGetProperty("reviewState", out var s) && s.ValueKind == JsonValueKind.String ? s.GetString() : null;
    }

    [Fact]
    public async Task The_window_opens_a_review_task_no_change_moves_the_date_without_attestation_and_a_passed_date_is_overdue()
    {
        var o = await FreshAsync();
        var t = o.T;
        var policy = await PublishedAsync(o, "TRV");
        var view = await OkAsync(t["co"].Client.GetAsync($"{Policies}/{policy}"));
        Assert.Equal(PolicyReviews.DefaultWindowDays, view.GetProperty("reviewWindowDays").GetInt32());
        Assert.Contains("validation.policies.review_window", await ErrorKeysAsync(await t["co"].Client.PostAsJsonAsync(Policies, new
        {
            code = "BAD", names = Titles, reviewWindowDays = 400,
        })));

        // The review date is a year away: no review yet.
        var now = DateTimeOffset.UtcNow;
        Assert.Equal(0, await RunDueAsync(t, now));
        Assert.Null(await RegisterStateAsync(t["sara"].Client, policy));
        Assert.Empty(await ReviewTasksAsync(t["sara"].Client));

        // 20 days before the review date the 60-day window is open: Sara has the task and the notice, once.
        var due = now.AddDays(20);
        await SetReviewDateAsync(t, policy, due);
        Assert.Equal(1, await RunDueAsync(t, now));
        Assert.Equal(0, await RunDueAsync(t, now));
        var task = Assert.Single(await ReviewTasksAsync(t["sara"].Client));
        Assert.Equal($"/policies/{policy}/reviews", task.GetProperty("link").GetString());
        Assert.Empty(await ReviewTasksAsync(t["co"].Client));
        Assert.Equal(1, await NotificationsAsync(t, "sara", PolicyReviews.ReviewDueTemplate));
        Assert.Equal("due_soon", await RegisterStateAsync(t["sara"].Client, policy));
        Assert.Equal("due_soon", await RegisterStateAsync(t["co"].Client, policy));
        Assert.Null(await RegisterStateAsync(t["emp"].Client, policy));

        // Employees are refused; another tenant does not find the policy.
        Assert.Equal(HttpStatusCode.Forbidden, (await t["emp"].Client.GetAsync($"{Policies}/{policy}/reviews")).StatusCode);
        var other = await world.CreateTenantAsync("prx-" + Guid.NewGuid().ToString("N")[..8], "workplace_plus", "en", ["en"]);
        await world.SeedUserAsync(other, "co", MembershipTypes.Employee);
        await world.AssignAsync(other, "co", BuiltInRoles.ComplianceOfficer, "tenant", null);
        Assert.Equal(HttpStatusCode.NotFound, (await other["co"].Client.GetAsync($"{Policies}/{policy}/reviews")).StatusCode);
        var reviews = await OkAsync(t["sara"].Client.GetAsync($"{Policies}/{policy}/reviews"));
        var reviewId = Id(reviews.GetProperty("open"));
        Assert.Equal(1, reviews.GetProperty("open").GetProperty("version").GetInt32());
        Assert.True(reviews.GetProperty("rights").GetProperty("complete").GetBoolean());
        Assert.False(reviews.GetProperty("rights").GetProperty("retire").GetBoolean());
        Assert.Equal(HttpStatusCode.NotFound, (await other["co"].Client.PostAsJsonAsync($"{Policies}/{policy}/reviews/{reviewId}/complete",
            new { outcome = "no_change" })).StatusCode);

        // The review date passes: overdue in the register, and compliance over the Gulf and Sara's manager hear of it, Egypt's officer does not.
        Assert.Equal(1, await RunDueAsync(t, due.AddHours(1)));
        Assert.Equal(0, await RunDueAsync(t, due.AddHours(2)));
        foreach (var user in new[] { "co", "gco", "mona", "sara" })
        {
            Assert.Equal(1, await NotificationsAsync(t, user, PolicyReviews.ReviewOverdueTemplate));
        }
        Assert.Equal(0, await NotificationsAsync(t, "egco", PolicyReviews.ReviewOverdueTemplate));
        Assert.Equal(0, await NotificationsAsync(t, "emp", PolicyReviews.ReviewOverdueTemplate));
        await SetReviewDateAsync(t, policy, now.AddHours(-1));
        Assert.Equal("overdue", await RegisterStateAsync(t["co"].Client, policy));
        await SetReviewDateAsync(t, policy, due);

        // An outcome is required; no change records Sara and the date and moves the review date by the cycle, with no campaign.
        Assert.Contains("validation.policies.review_outcome", await ErrorKeysAsync(await t["sara"].Client.PostAsJsonAsync($"{Policies}/{policy}/reviews/{reviewId}/complete",
            new { outcome = "revised" })));
        var done = await OkAsync(t["sara"].Client.PostAsJsonAsync($"{Policies}/{policy}/reviews/{reviewId}/complete", new { outcome = "no_change", note = "Still right" }));
        Assert.Equal(JsonValueKind.Null, done.GetProperty("open").ValueKind);
        var record = Assert.Single(done.GetProperty("history").EnumerateArray());
        Assert.Equal("no_change", record.GetProperty("outcome").GetString());
        Assert.Equal(t["sara"].Id, record.GetProperty("reviewer").GetProperty("id").GetGuid());
        Assert.Equal("Still right", record.GetProperty("note").GetString());
        var next = record.GetProperty("nextReviewAt").GetDateTimeOffset();
        Assert.Equal(due.AddMonths(12).ToUnixTimeSeconds(), next.ToUnixTimeSeconds());
        Assert.Equal(next.ToUnixTimeSeconds(), (await OkAsync(t["co"].Client.GetAsync($"{Policies}/{policy}"))).GetProperty("nextReviewAt").GetDateTimeOffset().ToUnixTimeSeconds());
        Assert.Equal(0, await world.Database.CountInTenantAsync(t.Id, "select count(*) from policies.campaign where policy_id = @p", ("p", policy)));
        await world.Database.AssertAuditedAsync(t.Id, AuditActions.PolicyReviewCompleted, policy.ToString());
        Assert.Empty(await ReviewTasksAsync(t["sara"].Client));
        Assert.Null(await RegisterStateAsync(t["co"].Client, policy));
        Assert.Contains("conflict.policies.review_done", await ErrorKeysAsync(await t["sara"].Client.PostAsJsonAsync($"{Policies}/{policy}/reviews/{reviewId}/complete",
            new { outcome = "no_change" })));
        Assert.Equal(0, await RunDueAsync(t, due.AddDays(1)));
    }

    [Fact]
    public async Task A_major_revision_starts_a_draft_and_asks_about_re_attestation_once_in_force_and_retire_needs_a_compliance_officer()
    {
        var o = await FreshAsync();
        var t = o.T;
        var now = DateTimeOffset.UtcNow;
        var policy = await PublishedAsync(o, "TRV");
        var other = await PublishedAsync(o, "GIFT");
        var third = await PublishedAsync(o, "CAR");
        foreach (var id in new[] { policy, other, third })
        {
            await SetReviewDateAsync(t, id, now.AddDays(10));
        }
        Assert.Equal(3, await RunDueAsync(t, now));
        Guid Open(JsonElement v) => Id(v.GetProperty("open"));

        // Major revision: a material draft copied from the version in force, which goes through the route.
        var reviews = await OkAsync(t["sara"].Client.GetAsync($"{Policies}/{policy}/reviews"));
        var done = await OkAsync(t["sara"].Client.PostAsJsonAsync($"{Policies}/{policy}/reviews/{Open(reviews)}/complete", new { outcome = "major_revision" }));
        var draftId = done.GetProperty("history")[0].GetProperty("draftVersionId").GetGuid();
        var draft = await OkAsync(t["sara"].Client.GetAsync($"{Policies}/{policy}/draft"));
        Assert.Equal(draftId, Id(draft));
        Assert.True(draft.GetProperty("material").GetBoolean());
        Assert.Equal("Travel", draft.GetProperty("clauses")[0].GetProperty("titles").GetProperty("en").GetString());
        Assert.Null(await RegisterStateAsync(t["co"].Client, policy));
        Assert.Equal(0, await NotificationsAsync(t, "sara", PolicyReviews.ReattestTemplate));
        await OkAsync(t["sara"].Client.PutAsJsonAsync($"{Policies}/{policy}/draft", new { titles = Titles, clauses = Clauses("Travel two"), material = true }));
        await SubmitAndApproveAsync(o, policy);
        await DispatchUntilAsync(async () => await NotificationsAsync(t, "sara", PolicyReviews.ReattestTemplate) == 1);
        var revised = await OkAsync(t["co"].Client.GetAsync($"{Policies}/{policy}"));
        Assert.Equal(2, revised.GetProperty("currentVersion").GetInt32());
        Assert.True(revised.GetProperty("nextReviewAt").GetDateTimeOffset() > now.AddMonths(11));

        // Minor update: a draft that is not material.
        var minor = await OkAsync(t["sara"].Client.GetAsync($"{Policies}/{other}/reviews"));
        await OkAsync(t["sara"].Client.PostAsJsonAsync($"{Policies}/{other}/reviews/{Open(minor)}/complete", new { outcome = "minor_update" }));
        Assert.False((await OkAsync(t["sara"].Client.GetAsync($"{Policies}/{other}/draft"))).GetProperty("material").GetBoolean());

        // Retire by the owner asks the compliance officers; the policy stays in force.
        var retire = await OkAsync(t["sara"].Client.GetAsync($"{Policies}/{third}/reviews"));
        await OkAsync(t["sara"].Client.PostAsJsonAsync($"{Policies}/{third}/reviews/{Open(retire)}/complete", new { outcome = "retire" }));
        Assert.Equal("published", (await OkAsync(t["co"].Client.GetAsync($"{Policies}/{third}"))).GetProperty("status").GetString());
        Assert.Equal(1, await NotificationsAsync(t, "co", PolicyReviews.RetireRequestedTemplate));
        Assert.Equal(1, await NotificationsAsync(t, "gco", PolicyReviews.RetireRequestedTemplate));
        Assert.Equal(0, await NotificationsAsync(t, "egco", PolicyReviews.RetireRequestedTemplate));

        // Next cycle: a compliance officer answering retire retires it.
        await SetReviewDateAsync(t, third, now.AddDays(5));
        Assert.Equal(1, await RunDueAsync(t, now));
        var again = await OkAsync(t["co"].Client.GetAsync($"{Policies}/{third}/reviews"));
        Assert.True(again.GetProperty("open").ValueKind == JsonValueKind.Object, again.GetRawText());
        Assert.True(again.GetProperty("rights").GetProperty("retire").GetBoolean());
        var retired = await OkAsync(t["co"].Client.PostAsJsonAsync($"{Policies}/{third}/reviews/{Open(again)}/complete", new { outcome = "retire" }));
        Assert.Equal("retired", retired.GetProperty("policy").GetProperty("status").GetString());
        Assert.Equal(2, retired.GetProperty("history").GetArrayLength());
        Assert.Equal(0, await RunDueAsync(t, now.AddDays(6)));
    }

    [Fact]
    public async Task A_version_in_force_before_the_outcome_closes_the_open_review_as_revised()
    {
        var o = await FreshAsync();
        var t = o.T;
        var now = DateTimeOffset.UtcNow;
        var policy = await PublishedAsync(o, "TRV", window: 30);
        await SetReviewDateAsync(t, policy, now.AddDays(40));
        Assert.Equal(0, await RunDueAsync(t, now));
        await SetReviewDateAsync(t, policy, now.AddDays(25));
        Assert.Equal(1, await RunDueAsync(t, now));

        // Sara revises through the editor instead of answering the review: the new version answers it.
        await OkAsync(t["sara"].Client.PutAsJsonAsync($"{Policies}/{policy}/draft", new { titles = Titles, clauses = Clauses("Travel two"), material = false }));
        await SubmitAndApproveAsync(o, policy);
        await DispatchUntilAsync(async () =>
            await world.Database.CountInTenantAsync(t.Id, "select count(*) from policies.policy_review where policy_id = @p and outcome = 'revised'", ("p", policy)) == 1);
        var reviews = await OkAsync(t["sara"].Client.GetAsync($"{Policies}/{policy}/reviews"));
        Assert.Equal(JsonValueKind.Null, reviews.GetProperty("open").ValueKind);
        var record = Assert.Single(reviews.GetProperty("history").EnumerateArray());
        Assert.Equal("revised", record.GetProperty("outcome").GetString());
        Assert.Equal(JsonValueKind.Null, record.GetProperty("reviewer").ValueKind);
        Assert.Empty(await ReviewTasksAsync(t["sara"].Client));
        Assert.Equal(0, await NotificationsAsync(t, "sara", PolicyReviews.ReattestTemplate));
    }
}
