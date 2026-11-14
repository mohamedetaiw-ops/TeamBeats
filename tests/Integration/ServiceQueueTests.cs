using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TeamBeats.Authorization.Contracts;
using TeamBeats.Messaging;
using TeamBeats.Modules.Services;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Modules.Workflow.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Testing;
using Xunit;

namespace TeamBeats.Integration.Tests;

/// <summary>
/// Spec 016 R2 T023 (US6): a queue task is picked by one member, so the rest of the queue stops seeing it until it is
/// put back; only the picker completes it or rejects it with a reason; a pick lapses when the picker leaves the queue;
/// another tenant cannot touch it; a failed automatic step alerts the fulfilment queue and the owners.
/// </summary>
[Collection(WorldCollection.Name)]
public sealed class ServiceQueueTests(TenantWorld world)
{
    private const string Admin = "/api/v1/services/admin";

    private static Dictionary<string, string> Names(string en, string ar) => new() { ["en"] = en, ["ar"] = ar };

    private async Task<TestTenant> FreshAsync()
    {
        var t = await world.CreateTenantAsync("que-" + Guid.NewGuid().ToString("N")[..8], "workplace_plus");
        var holding = await world.CreateUnitAsync(t, "Holding", null, "organization");
        await world.CreateUnitAsync(t, "Dubai", holding, "department");
        foreach (var name in new[] { "sara", "lina", "kofi" })
        {
            await world.SeedUserAsync(t, name, MembershipTypes.Employee);
            await world.AddMembershipAsync(t, name, "Dubai");
        }
        return t;
    }

    private Task<Guid> GroupAsync(TestTenant t, string key, params string[] members) =>
        world.Factory.InScopeAsync(t.Id, async sp =>
        {
            var id = await sp.GetRequiredService<IAuthorizationProvisioning>().SyncDirectoryGroupAsync(key, key, members.Select(m => t[m].Id).ToList());
            await sp.GetRequiredService<IDbSession>().CommitAsync();
            return id;
        });

    private static object Step(string id, string type, object? extra = null)
    {
        var step = new Dictionary<string, object?> { ["id"] = id, ["type"] = type, ["names"] = Names("Print cards", "طباعة البطاقات") };
        foreach (var p in (extra ?? new { }).GetType().GetProperties())
        {
            step[p.Name] = p.GetValue(extra);
        }
        return step;
    }

    private static async Task<Guid> ServiceAsync(TestTenant t, Guid queue)
    {
        var created = await t["admin"].Client.PostAsJsonAsync(Admin, new { kind = "form", names = Names("Business cards", "بطاقات العمل") });
        var id = (await created.ReadAsync<JsonElement>()).GetProperty("service").GetProperty("id").GetGuid();
        var draft = await t["admin"].Client.PutAsJsonAsync($"{Admin}/{id}/draft", new
        {
            form = new { fields = new object[] { new { key = "boxes", type = "number", labels = Names("Boxes", "علب") } } },
            route = new
            {
                graph = new
                {
                    steps = new[] { Step("print", "task", new { assignee = new { kind = "group", groups = new[] { queue } } }), Step("done", "end") },
                    links = new[] { new { from = "print", to = "done" } },
                },
            },
            fulfilmentQueueGroupId = queue,
        });
        Assert.True(draft.StatusCode == HttpStatusCode.OK, await draft.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.OK, (await t["admin"].Client.PostAsJsonAsync($"{Admin}/{id}/publish", new { })).StatusCode);
        return id;
    }

    private static async Task<Guid> SubmitAsync(HttpClient client, Guid service)
    {
        var response = await client.PostAsJsonAsync("/api/v1/requests", new { serviceId = service, data = new { boxes = 2 } });
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return (await response.ReadAsync<JsonElement>()).GetProperty("request").GetProperty("id").GetGuid();
    }

    private static async Task<JsonElement[]> InboxAsync(HttpClient client) =>
        (await (await client.GetAsync("/api/v1/tasks")).ReadAsync<JsonElement>()).EnumerateArray().ToArray();

    private static async Task<JsonElement?> ItemAsync(HttpClient client, Guid request) =>
        (await InboxAsync(client)).Cast<JsonElement?>().FirstOrDefault(i => i!.Value.GetProperty("requestId").GetGuid() == request);

    private static async Task<JsonElement> DetailAsync(HttpClient client, Guid id) => await (await client.GetAsync($"/api/v1/requests/{id}")).ReadAsync<JsonElement>();

    private static async Task<List<string>> TrackingAsync(HttpClient client, Guid id) =>
        (await DetailAsync(client, id)).GetProperty("tracking").EnumerateArray().Select(e => e.GetProperty("event").GetString()!).ToList();

    private static Task<HttpResponseMessage> DecideAsync(HttpClient client, Guid task, string outcome, string? comment = null) =>
        client.PostAsJsonAsync($"/api/v1/tasks/{task}/decision", new { outcome, comment });

    [Fact]
    public async Task One_member_picks_a_queue_task_the_rest_stop_seeing_it_and_only_the_picker_completes_it()
    {
        var t = await FreshAsync();
        var queue = await GroupAsync(t, "logistics", "lina", "kofi");
        var service = await ServiceAsync(t, queue);
        var request = await SubmitAsync(t["sara"].Client, service);

        var item = (await ItemAsync(t["lina"].Client, request))!.Value;
        Assert.True(item.GetProperty("pickable").GetBoolean());
        Assert.Equal(JsonValueKind.Null, item.GetProperty("pickedBy").ValueKind);
        var task = item.GetProperty("taskId").GetGuid();
        Assert.NotNull(await ItemAsync(t["kofi"].Client, request));

        // Lina picks it: Kofi no longer sees it, cannot pick it or decide it; picking again changes nothing.
        Assert.Equal(HttpStatusCode.OK, (await t["lina"].Client.PostAsync($"/api/v1/tasks/{task}/pick", null)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await t["lina"].Client.PostAsync($"/api/v1/tasks/{task}/pick", null)).StatusCode);
        Assert.Equal(t["lina"].Id, (await ItemAsync(t["lina"].Client, request))!.Value.GetProperty("pickedBy").GetProperty("id").GetGuid());
        Assert.Null(await ItemAsync(t["kofi"].Client, request));
        Assert.Equal(HttpStatusCode.Forbidden, (await t["kofi"].Client.PostAsync($"/api/v1/tasks/{task}/pick", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await DecideAsync(t["kofi"].Client, task, "approve")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await t["kofi"].Client.DeleteAsync($"/api/v1/tasks/{task}/pick")).StatusCode);

        // Another tenant gets not found.
        var other = await FreshAsync();
        Assert.Equal(HttpStatusCode.NotFound, (await other["admin"].Client.PostAsync($"/api/v1/tasks/{task}/pick", null)).StatusCode);

        // Put back: Kofi sees it again, picks it, and must give a reason to reject it.
        Assert.Equal(HttpStatusCode.OK, (await t["lina"].Client.DeleteAsync($"/api/v1/tasks/{task}/pick")).StatusCode);
        Assert.NotNull(await ItemAsync(t["kofi"].Client, request));
        Assert.Equal(HttpStatusCode.OK, (await t["kofi"].Client.PostAsync($"/api/v1/tasks/{task}/pick", null)).StatusCode);
        Assert.Null(await ItemAsync(t["lina"].Client, request));
        Assert.Equal(HttpStatusCode.BadRequest, (await DecideAsync(t["kofi"].Client, task, "reject")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await DecideAsync(t["kofi"].Client, task, "reject", "Out of card stock")).StatusCode);
        Assert.Equal("rejected", (await DetailAsync(t["sara"].Client, request)).GetProperty("request").GetProperty("status").GetString());
        Assert.Equal(["picked", "put_back", "picked"], (await TrackingAsync(t["sara"].Client, request)).Where(e => e is "picked" or "put_back"));
    }

    [Fact]
    public async Task A_pick_lapses_when_the_picker_leaves_the_queue()
    {
        var t = await FreshAsync();
        var queue = await GroupAsync(t, "logistics", "lina", "kofi");
        var service = await ServiceAsync(t, queue);
        var request = await SubmitAsync(t["sara"].Client, service);
        var task = (await ItemAsync(t["lina"].Client, request))!.Value.GetProperty("taskId").GetGuid();
        Assert.Equal(HttpStatusCode.OK, (await t["lina"].Client.PostAsync($"/api/v1/tasks/{task}/pick", null)).StatusCode);
        Assert.Null(await ItemAsync(t["kofi"].Client, request));

        await GroupAsync(t, "logistics", "kofi");
        Assert.Null(await ItemAsync(t["lina"].Client, request));
        Assert.NotNull(await ItemAsync(t["kofi"].Client, request));
        Assert.Equal(HttpStatusCode.OK, (await DecideAsync(t["kofi"].Client, task, "approve")).StatusCode);
        Assert.Equal("completed", (await DetailAsync(t["sara"].Client, request)).GetProperty("request").GetProperty("status").GetString());
    }

    [Fact]
    public async Task A_failed_automatic_step_alerts_the_fulfilment_queue_and_the_owners_once()
    {
        var t = await FreshAsync();
        var queue = await GroupAsync(t, "logistics", "lina", "kofi");
        var service = await ServiceAsync(t, queue);
        var request = await SubmitAsync(t["sara"].Client, service);
        var (instance, key, business) = await world.Factory.InScopeAsync(t.Id, async sp =>
        {
            var id = await world.Database.TenantScalarAsync<Guid>(t.Id, $"select process_instance_id from services.request where id = '{request}'");
            var info = (await sp.GetRequiredService<IProcessEngine>().GetInstanceAsync(id))!;
            return (id, info.DefinitionKey, info.BusinessKey);
        });
        var failure = new ProcessIncidentRaised(Guid.NewGuid(), instance, key, business, "print", "connector");
        for (var i = 0; i < 2; i++)
        {
            await world.Factory.InScopeAsync(t.Id, async sp =>
            {
                await sp.GetRequiredService<Automation>().HandleAsync(failure, CancellationToken.None);
                await sp.GetRequiredService<IDbSession>().CommitAsync();
                return 0;
            });
        }
        await world.Factory.Services.GetRequiredService<OutboxDispatcher>().DispatchBatchAsync(500, CancellationToken.None);
        foreach (var person in new[] { "lina", "kofi", "admin" })
        {
            var items = (await (await t[person].Client.GetAsync("/api/v1/me/notifications")).ReadAsync<JsonElement>()).GetProperty("items").EnumerateArray();
            Assert.Single(items, n => n.GetProperty("link").GetString() == $"/requests/{request}"
                && n.GetProperty("title").GetString()!.Contains("failed", StringComparison.Ordinal));
        }
        var sara = (await (await t["sara"].Client.GetAsync("/api/v1/me/notifications")).ReadAsync<JsonElement>()).GetProperty("items").EnumerateArray();
        Assert.DoesNotContain(sara, n => n.GetProperty("title").GetString()!.Contains("failed", StringComparison.Ordinal));
    }
}
