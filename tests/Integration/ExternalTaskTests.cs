using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TeamBeats.Audit.Contracts;
using TeamBeats.Entitlements.Contracts;
using TeamBeats.Modules.Integrations.Ticketing;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Modules.Workflow;
using TeamBeats.Persistence;
using TeamBeats.Primitives;
using TeamBeats.Testing;
using Xunit;

namespace TeamBeats.Integration.Tests;

/// <summary>
/// Spec 024 R3 T021 (US8, FR-016): Camunda 8 user tasks assigned to TeamBeats people appear in their inbox, are
/// completed from TeamBeats with the outcome and comment as variables, a failure is shown and retried, and only the
/// task reference is kept. Camunda is a fake host of the test factory.
/// </summary>
[Collection(WorldCollection.Name)]
public sealed class ExternalTaskTests(TenantWorld world)
{
    private const string Packs = "/api/v1/integrations/pack-connections";
    private const string Tasks = "/api/v1/workflow/external-tasks";

    private FakeRestApi Rest => world.Factory.Rest;

    /// <summary>A small Camunda 8 cluster: open user tasks, and completions that can be refused or fail.</summary>
    private sealed class FakeCamunda
    {
        public ConcurrentDictionary<string, (string Name, string? Assignee)> Open { get; } = new();
        public ConcurrentDictionary<string, string> Completed { get; } = new();
        public HttpStatusCode? CompletionAnswer;

        public HttpResponseMessage Respond(FakeRestApi.Sent s)
        {
            if (s.Method == "GET" && s.Uri.AbsolutePath == "/v2/topology")
            {
                return FakeRestApi.Json(HttpStatusCode.OK, """{"clusterSize":1,"brokers":[]}""");
            }
            if (s.Uri.AbsolutePath == "/v2/user-tasks/search")
            {
                var items = Open.OrderBy(t => t.Key).Select(t => new
                {
                    userTaskKey = t.Key, name = t.Value.Name, assignee = t.Value.Assignee, processName = "Purchase", state = "CREATED",
                    creationDate = "2026-11-24T08:00:00.000Z", dueDate = "2026-11-27T17:00:00.000Z", variables = new { secret = "kept in Camunda" },
                });
                return FakeRestApi.Json(HttpStatusCode.OK, JsonSerializer.Serialize(new { items, page = new { totalItems = Open.Count } }));
            }
            var key = s.Uri.AbsolutePath.Split('/')[3];
            if (CompletionAnswer is { } refused)
            {
                return FakeRestApi.Json(refused, """{"title":"refused"}""");
            }
            if (!Open.TryRemove(key, out _))
            {
                return FakeRestApi.Json(HttpStatusCode.NotFound, """{"title":"NOT_FOUND"}""");
            }
            Completed[key] = s.Body!;
            return new HttpResponseMessage(HttpStatusCode.NoContent);
        }
    }

    private async Task<(TestTenant T, string Host, FakeCamunda Fake, Guid Connection)> FreshAsync(bool advanced = true)
    {
        var t = await world.CreateTenantAsync("ext-" + Guid.NewGuid().ToString("N")[..8], "workplace_plus");
        await world.SeedUserAsync(t, "mona", MembershipTypes.Employee);
        await world.SeedUserAsync(t, "omar", MembershipTypes.Employee);
        if (advanced)
        {
            await world.SetCapabilityAsync(t, Capabilities.WorkflowBpmnAdvanced, new { enabled = true });
        }
        var host = $"camunda-{Guid.NewGuid():N}"[..20] + ".rest.test";
        Assert.Equal(HttpStatusCode.OK, (await t["admin"].Client.PostAsJsonAsync("/api/v1/integrations/allowlist", new { host })).StatusCode);
        var fake = new FakeCamunda();
        Rest.On(host, fake.Respond);
        if (!advanced)
        {
            return (t, host, fake, Guid.Empty);
        }
        world.Factory.Secrets.Set(SecretReferences.For(t.Id, "camunda"), "fictional-camunda-password");
        var created = await t["admin"].Client.PostAsJsonAsync(Packs, new { kind = Camunda8Pack.KindName, displayName = "Camunda", settings = Settings(t, host) });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var id = (await created.ReadAsync<JsonElement>()).GetProperty("id").GetGuid();
        var test = await (await t["admin"].Client.PostAsync($"{Packs}/{id}:test", null)).ReadAsync<JsonElement>();
        Assert.Equal("ok", test.GetProperty("status").GetString());
        Assert.Equal(HttpStatusCode.OK, (await t["admin"].Client.PostAsync($"{Packs}/{id}:enable", null)).StatusCode);
        return (t, host, fake, id);
    }

    private static object Settings(TestTenant t, string host) =>
        new { baseUrl = $"https://{host}", auth = new { type = "basic", username = "teambeats", secretRef = SecretReferences.For(t.Id, "camunda") } };

    private Task<int> SyncAsync(TestTenant t) => world.Factory.InScopeAsync(t.Id, async sp =>
    {
        var found = await sp.GetRequiredService<ExternalTasks>().SyncAsync(CancellationToken.None);
        await sp.GetRequiredService<IDbSession>().CommitAsync();
        return found;
    });

    private Task<int> RetryAsync(TestTenant t) => world.Factory.InScopeAsync(t.Id, async sp =>
    {
        var sent = await sp.GetRequiredService<ExternalTasks>().RetryAsync(CancellationToken.None);
        await sp.GetRequiredService<IDbSession>().CommitAsync();
        return sent;
    });

    private static async Task<List<JsonElement>> InboxAsync(TestTenant t, string user) =>
        (await (await t[user].Client.GetAsync("/api/v1/tasks")).ReadAsync<JsonElement>()).EnumerateArray()
            .Where(i => i.GetProperty("source").GetString() == ExternalTasks.Source).ToList();

    private static Guid IdOf(JsonElement item) => item.GetProperty("taskId").GetGuid();

    [Fact]
    public async Task A_Camunda_task_for_a_TeamBeats_person_is_in_their_inbox_and_is_completed_with_the_outcome_and_comment()
    {
        var (t, host, fake, _) = await FreshAsync();
        fake.Open["2251799813685249"] = ("Check the quote", t["mona"].Email);
        fake.Open["2251799813685250"] = ("Sign the order", "someone@elsewhere.example");
        fake.Open["2251799813685251"] = ("Unassigned", null);

        Assert.Equal(1, await SyncAsync(t));
        Assert.Equal(1, await SyncAsync(t));
        var item = Assert.Single(await InboxAsync(t, "mona"));
        Assert.Equal("Check the quote", item.GetProperty("title").GetProperty("en").GetString());
        Assert.Equal(ExternalTasks.Link(IdOf(item)), item.GetProperty("link").GetString());
        Assert.Empty(await InboxAsync(t, "omar"));

        // Only the assignee sees it; another person, or another tenant, does not find it.
        Assert.Equal(HttpStatusCode.NotFound, (await t["omar"].Client.GetAsync($"{Tasks}/{IdOf(item)}")).StatusCode);
        var other = await FreshAsync();
        Assert.Equal(HttpStatusCode.NotFound, (await other.T["mona"].Client.GetAsync($"{Tasks}/{IdOf(item)}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await t["omar"].Client.PostAsJsonAsync($"{Tasks}/{IdOf(item)}:complete", new { outcome = "approved" })).StatusCode);

        var view = await (await t["mona"].Client.GetAsync($"{Tasks}/{IdOf(item)}")).ReadAsync<JsonElement>();
        Assert.Equal("open", view.GetProperty("status").GetString());
        Assert.Equal("Purchase", view.GetProperty("processName").GetString());
        Assert.Equal(DateTimeOffset.Parse("2026-11-27T17:00:00Z"), view.GetProperty("dueAt").GetDateTimeOffset());

        Assert.Equal(HttpStatusCode.BadRequest, (await t["mona"].Client.PostAsJsonAsync($"{Tasks}/{IdOf(item)}:complete", new { outcome = "maybe" })).StatusCode);
        var done = await (await t["mona"].Client.PostAsJsonAsync($"{Tasks}/{IdOf(item)}:complete", new { outcome = "approved", comment = "Within budget" }))
            .ReadAsync<JsonElement>();
        Assert.Equal("completed", done.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, done.GetProperty("outcome").ValueKind);
        Assert.Equal(JsonValueKind.Null, done.GetProperty("comment").ValueKind);

        var sent = JsonDocument.Parse(fake.Completed["2251799813685249"]).RootElement.GetProperty("variables");
        Assert.Equal("approved", sent.GetProperty("outcome").GetString());
        Assert.Equal("Within budget", sent.GetProperty("comment").GetString());
        Assert.Equal("/v2/user-tasks/2251799813685249/completion", Rest.SentTo(host).Last(s => s.Method == "POST").Uri.AbsolutePath);
        Assert.Empty(await InboxAsync(t, "mona"));
        Assert.Equal(HttpStatusCode.Conflict, (await t["mona"].Client.PostAsJsonAsync($"{Tasks}/{IdOf(item)}:complete", new { outcome = "approved" })).StatusCode);

        // TeamBeats keeps the reference and its own audit, never Camunda's variables.
        Assert.Equal(1L, await world.Database.TenantScalarAsync<long>(t.Id, "select count(*) from audit.audit_event where action = @a", ("a", AuditActions.ProcessExternalTaskCompleted)));
        Assert.Equal(0L, await world.Database.TenantScalarAsync<long>(t.Id, "select count(*) from workflow.external_task where name like '%kept in Camunda%' or comment is not null"));
    }

    [Fact]
    public async Task A_failed_completion_is_shown_on_the_task_and_retried_and_a_task_closed_in_Camunda_leaves_the_inbox()
    {
        var (t, _, fake, _) = await FreshAsync();
        fake.Open["100"] = ("Review contract", t["mona"].Email);
        fake.Open["101"] = ("Second review", t["mona"].Email);
        await SyncAsync(t);
        var items = await InboxAsync(t, "mona");
        Assert.Equal(2, items.Count);
        var first = items.Single(i => i.GetProperty("title").GetProperty("en").GetString() == "Review contract");

        fake.CompletionAnswer = HttpStatusCode.ServiceUnavailable;
        var failed = await (await t["mona"].Client.PostAsJsonAsync($"{Tasks}/{IdOf(first)}:complete", new { outcome = "rejected", comment = "Wrong supplier" }))
            .ReadAsync<JsonElement>();
        Assert.Equal("failed", failed.GetProperty("status").GetString());
        Assert.False(string.IsNullOrEmpty(failed.GetProperty("lastError").GetString()));
        Assert.Equal(JsonValueKind.String, failed.GetProperty("nextAttemptAt").ValueKind);
        Assert.Equal("rejected", failed.GetProperty("outcome").GetString());
        Assert.Contains(await InboxAsync(t, "mona"), i => IdOf(i) == IdOf(first));

        // Not due yet: nothing is sent. Once due, the worker sends it again and it goes through.
        Assert.Equal(0, await RetryAsync(t));
        fake.CompletionAnswer = null;
        await world.Factory.InScopeAsync(t.Id, async sp =>
        {
            var session = sp.GetRequiredService<IDbSession>();
            await session.ExecAsync("update workflow.external_task set next_attempt_at = now() - interval '1 minute' where id = @id", new { id = IdOf(first) });
            await session.CommitAsync();
            return 0;
        });
        Assert.Equal(1, await RetryAsync(t));
        var retried = await (await t["mona"].Client.GetAsync($"{Tasks}/{IdOf(first)}")).ReadAsync<JsonElement>();
        Assert.Equal("completed", retried.GetProperty("status").GetString());
        Assert.Equal(2, retried.GetProperty("attempts").GetInt32());
        Assert.Equal("Wrong supplier", JsonDocument.Parse(fake.Completed["100"]).RootElement.GetProperty("variables").GetProperty("comment").GetString());

        // Completed in Camunda itself: the next sync closes it here.
        fake.Open.TryRemove("101", out _);
        await SyncAsync(t);
        Assert.Empty(await InboxAsync(t, "mona"));
        var second = items.Single(i => IdOf(i) != IdOf(first));
        Assert.Equal("closed", (await (await t["mona"].Client.GetAsync($"{Tasks}/{IdOf(second)}")).ReadAsync<JsonElement>()).GetProperty("status").GetString());
    }

    [Fact]
    public async Task Camunda_needs_the_advanced_process_package()
    {
        var (t, host, fake, _) = await FreshAsync(advanced: false);
        world.Factory.Secrets.Set(SecretReferences.For(t.Id, "camunda"), "fictional-camunda-password");
        var refused = await t["admin"].Client.PostAsJsonAsync(Packs, new { kind = Camunda8Pack.KindName, displayName = "Camunda", settings = Settings(t, host) });
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        fake.Open["1"] = ("Check", t["mona"].Email);
        Assert.Equal(0, await SyncAsync(t));
        Assert.Empty(await InboxAsync(t, "mona"));
        Assert.Equal(HttpStatusCode.Forbidden, (await t["mona"].Client.GetAsync($"{Tasks}/{Ids.New()}")).StatusCode);
    }
}
