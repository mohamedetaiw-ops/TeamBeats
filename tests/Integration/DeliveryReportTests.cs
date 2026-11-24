using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TeamBeats.Authorization.Contracts;
using TeamBeats.Messaging;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Testing;
using Xunit;

namespace TeamBeats.Integration.Tests;

/// <summary>
/// Spec 015 US8 (R2, FR-022): the delivery report of a critical announcement. People who can send critical news see
/// how many people were reached and each channel by status (delivered, read, waiting, failed, bounced, not sent) as
/// counts, never names. Only critical announcements have one, other people are refused, and tenants stay apart.
/// </summary>
[Collection(WorldCollection.Name)]
public sealed class DeliveryReportTests(TenantWorld world)
{
    private const string Items = "/api/v1/content/items";

    private sealed record Org(TestTenant T, Guid Plant);

    private async Task<Org> FreshAsync()
    {
        var t = await world.CreateTenantAsync("dr-" + Guid.NewGuid().ToString("N")[..8], "workplace_plus");
        var holding = await world.CreateUnitAsync(t, "Holding", null, "organization");
        var plant = await world.CreateUnitAsync(t, "Plant", holding, "department");
        foreach (var name in new[] { "hs", "pia", "ali", "bea", "cem" })
        {
            await world.SeedUserAsync(t, name, MembershipTypes.Employee);
        }
        foreach (var name in new[] { "ali", "bea", "cem" })
        {
            await world.AddMembershipAsync(t, name, "Plant");
        }
        await world.AssignAsync(t, "pia", BuiltInRoles.ContentPublisher, "tenant", null);
        var role = await t["admin"].Client.PostAsJsonAsync("/api/v1/authz/roles", new
        {
            key = "safety_lead", names = new Dictionary<string, string> { ["en"] = "Safety lead" },
            permissions = new[] { Permissions.ContentView, Permissions.ContentAuthor, Permissions.ContentEdit, Permissions.ContentPublish, Permissions.ContentPublishCritical },
            delegable = true,
        });
        Assert.Equal(HttpStatusCode.Created, role.StatusCode);
        await world.AssignAsync(t, "hs", "safety_lead", "tenant", null);
        await DispatchAsync();
        return new Org(t, plant);
    }

    private static object Variants(string title) => new Dictionary<string, object>
    {
        ["en"] = new { title, body = new { blocks = new object[] { new { type = "paragraph", children = new object[] { new { text = title } } } } } },
    };

    private async Task<Guid> PublishAsync(HttpClient client, string title, Guid plant, object? critical)
    {
        var audience = new { anyOf = new[] { new { orgUnits = new[] { new { id = plant } } } } };
        var created = await client.PostAsJsonAsync(Items, new { type = "announcement", variants = Variants(title), audience, critical });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var id = (await created.ReadAsync<JsonElement>()).GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync($"{Items}/{id}/publish", new { material = true })).StatusCode);
        await DispatchAsync();
        return id;
    }

    private async Task DispatchAsync()
    {
        var dispatcher = world.Factory.Services.GetRequiredService<OutboxDispatcher>();
        await dispatcher.DispatchBatchAsync(500, CancellationToken.None);
        await dispatcher.DispatchBatchAsync(500, CancellationToken.None);
    }

    private static string Link(Guid id) => $"/content/read/{id}";

    private static JsonElement Channel(JsonElement report, string channel) =>
        report.GetProperty("channels").EnumerateArray().Single(c => c.GetProperty("channel").GetString() == channel);

    [Fact]
    public async Task The_report_counts_people_and_each_channel_by_status_without_names()
    {
        var o = await FreshAsync();
        var hs = o.T["hs"].Client;
        var id = await PublishAsync(hs, "Evacuation route changed", o.Plant, new { presentation = "modal" });

        // Set each channel's outcome directly so every status is counted: ali's email sent, bea's bounced, cem's failed
        // for another reason, plus a waiting push and a dropped Teams message.
        await world.Database.OwnerExecInTenantAsync(o.T.Id,
            "delete from notifications.delivery d using notifications.notification n where n.tenant_id = d.tenant_id and n.id = d.notification_id and n.link = @l",
            ("l", Link(id)));
        string[] plan = ["ali:email:sent", "bea:email:failed", "cem:email:failed", "ali:push:pending", "bea:teams:dropped"];
        foreach (var step in plan)
        {
            var parts = step.Split(':');
            await world.Database.OwnerExecInTenantAsync(o.T.Id,
                """
                insert into notifications.delivery (tenant_id, id, notification_id, user_id, channel, status, address, not_before, created_at)
                select n.tenant_id, gen_random_uuid(), n.id, n.user_id, @c, @s, @a, now(), now()
                from notifications.notification n where n.link = @l and n.user_id = @u
                order by n.created_at limit 1
                """, ("l", Link(id)), ("u", o.T[parts[0]].Id), ("c", parts[1]), ("s", parts[2]), ("a", parts[0] + "@example.test"));
        }
        await world.Database.OwnerExecInTenantAsync(o.T.Id,
            "insert into notifications.address_state (tenant_id, address, bounced_at, reason) values (current_setting('app.tenant_id')::uuid, 'bea@example.test', now(), 'hard')");
        await world.Database.OwnerExecInTenantAsync(o.T.Id,
            "update notifications.notification set read_at = now() where link = @l and user_id = @u", ("l", Link(id)), ("u", o.T["ali"].Id));

        var response = await hs.GetAsync($"{Items}/{id}/delivery-report");
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        var report = await response.ReadAsync<JsonElement>();
        Assert.Equal(3, report.GetProperty("recipients").GetInt32());
        var notifications = report.GetProperty("notifications").GetInt32();
        Assert.True(notifications >= 3);

        var inApp = Channel(report, "in_app");
        Assert.Equal(notifications, inApp.GetProperty("delivered").GetInt32());
        Assert.True(inApp.GetProperty("read").GetInt32() >= 1);
        Assert.Equal(JsonValueKind.Null, inApp.GetProperty("bounced").ValueKind);

        var email = Channel(report, "email");
        Assert.Equal(1, email.GetProperty("delivered").GetInt32());
        Assert.Equal(1, email.GetProperty("bounced").GetInt32());
        Assert.Equal(1, email.GetProperty("failed").GetInt32());
        Assert.Equal(JsonValueKind.Null, email.GetProperty("read").ValueKind);
        Assert.Equal(1, Channel(report, "push").GetProperty("pending").GetInt32());
        Assert.Equal(JsonValueKind.Null, Channel(report, "push").GetProperty("bounced").ValueKind);
        Assert.Equal(1, Channel(report, "teams").GetProperty("dropped").GetInt32());

        // Counts only: no person, address or user id anywhere in the answer.
        var text = report.GetRawText();
        foreach (var name in new[] { "ali", "bea", "cem" })
        {
            Assert.DoesNotContain(o.T[name].Id.ToString(), text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(name + "@", text, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task Only_critical_items_have_a_report_and_only_critical_senders_or_admins_see_it()
    {
        var o = await FreshAsync();
        var hs = o.T["hs"].Client;
        var critical = await PublishAsync(hs, "Gas leak drill", o.Plant, new { presentation = "banner" });
        var plain = await PublishAsync(o.T["pia"].Client, "Canteen menu", o.Plant, null);

        var notCritical = await hs.GetAsync($"{Items}/{plain}/delivery-report");
        Assert.Equal(HttpStatusCode.Conflict, notCritical.StatusCode);

        // A publisher without the critical permission and an employee are refused; the tenant admin may read it.
        Assert.Equal(HttpStatusCode.Forbidden, (await o.T["pia"].Client.GetAsync($"{Items}/{critical}/delivery-report")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await o.T["ali"].Client.GetAsync($"{Items}/{critical}/delivery-report")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await o.T["admin"].Client.GetAsync($"{Items}/{critical}/delivery-report")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await hs.GetAsync($"{Items}/{Guid.NewGuid()}/delivery-report")).StatusCode);

        // Another tenant's admin cannot reach it.
        var other = await FreshAsync();
        Assert.Equal(HttpStatusCode.NotFound, (await other.T["admin"].Client.GetAsync($"{Items}/{critical}/delivery-report")).StatusCode);
    }
}
