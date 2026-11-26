using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TeamBeats.Modules.Identity.Contracts;
using TeamBeats.Modules.Integrations.WebPush;
using TeamBeats.Modules.Notifications;
using TeamBeats.Modules.Notifications.Contracts;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Messaging;
using TeamBeats.Persistence;
using TeamBeats.Testing;
using Xunit;

namespace TeamBeats.Integration.Tests;

/// <summary>
/// Spec 030 R3 MOB-022 (T012, T013): native push for the store apps. The app registers the token the messaging service
/// gave it; deliveries carry only the category and a link, as Web Push does; a token the service reports gone is
/// removed at once; web and native devices share the limit of 10; signing out removes the device; the native shell
/// feature decides, and tenants never reach each other's devices.
/// </summary>
[Collection(WorldCollection.Name)]
public sealed class NativePushTests(TenantWorld world)
{
    private const string Api = "/api/v1/me/push";

    private FakeFcm Fcm => world.Factory.Fcm;

    private static string NewToken() => "dev" + Convert.ToHexString(RandomNumberGenerator.GetBytes(40)).ToLowerInvariant() + ":APA91b";

    private async Task<TestTenant> TenantAsync(string package = "enterprise_gov")
    {
        var t = await world.CreateTenantAsync("np-" + Guid.NewGuid().ToString("N")[..8], package);
        await world.SeedUserAsync(t, "employee", MembershipTypes.Employee);
        return t;
    }

    private static async Task<JsonElement> RegisterAsync(TestUser user, string token, string platform = "android")
    {
        var response = await user.Client.PostAsJsonAsync($"{Api}/native", new { token, platform });
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, text);
        return JsonDocument.Parse(text).RootElement;
    }

    private Task SendAsync(TestTenant t, string category, Guid user, bool urgent = false) =>
        world.Factory.InScopeAsync(t.Id, async sp =>
        {
            await sp.GetRequiredService<INotificationSender>().SendAsync(new NotificationRequest([user], category, "system.sample", "/requests/42",
                new Dictionary<string, string> { ["label"] = "Project Falcon salary review", ["count"] = "3" }, Urgent: urgent));
            await sp.GetRequiredService<IDbSession>().CommitAsync();
            return true;
        });

    private Task<int> DeliverAsync(TestTenant t) =>
        world.Factory.InScopeAsync(t.Id, async sp =>
        {
            var count = await sp.GetRequiredService<PushDelivery>().DeliverDueAsync(DateTimeOffset.UtcNow.AddSeconds(1), 100, CancellationToken.None);
            await sp.GetRequiredService<IDbSession>().CommitAsync();
            return count;
        });

    private Task<long> NativeAsync(TestTenant t, Guid user) =>
        world.Database.CountInTenantAsync(t.Id, "select count(*) from notifications.native_push_device where user_id = @u", ("u", user));

    private Task DispatchAsync() => world.Factory.Services.GetRequiredService<OutboxDispatcher>().DispatchBatchAsync(500, CancellationToken.None);

    [Fact]
    public async Task The_app_registers_and_gets_a_message_with_only_the_category_and_a_link()
    {
        var t = await TenantAsync();
        var employee = t["employee"];
        var token = NewToken();

        var status = await employee.Client.GetFromJsonAsync<JsonElement>(Api);
        Assert.True(status.GetProperty("nativeAvailable").GetBoolean());
        Assert.Equal(1, (await RegisterAsync(employee, token)).GetProperty("devices").GetInt32());
        // Registering the same token again is the same device.
        Assert.Equal(1, (await RegisterAsync(employee, token)).GetProperty("devices").GetInt32());

        await SendAsync(t, NotificationCategories.RequestUpdate, employee.Id, urgent: true);
        Assert.Equal(1, await DeliverAsync(t));
        var message = Assert.Single(Fcm.To(token));
        Assert.Equal("Bearer test-access", message.Authorization);
        var m = message.Message;
        Assert.Equal("Updates on your requests", m.GetProperty("notification").GetProperty("title").GetString());
        Assert.Equal("Open TeamBeats to read it.", m.GetProperty("notification").GetProperty("body").GetString());
        Assert.Equal("/requests/42", m.GetProperty("data").GetProperty("link").GetString());
        Assert.Equal("high", m.GetProperty("android").GetProperty("priority").GetString());
        Assert.Equal("86400s", m.GetProperty("android").GetProperty("ttl").GetString());
        Assert.Equal("10", m.GetProperty("apns").GetProperty("headers").GetProperty("apns-priority").GetString());
        // Nothing the notification itself says leaves in a push (spec 015 NOT-007).
        Assert.DoesNotContain("Falcon", m.GetRawText(), StringComparison.Ordinal);
        Assert.Equal(1, await world.Database.CountInTenantAsync(t.Id, "select count(*) from notifications.delivery where channel = 'push' and status = 'sent'"));

        // Only a real registration and a store platform are taken.
        foreach (var (bad, platform, key) in new[] { ("short", "android", "validation.push.native_token"), (NewToken() + "<script>", "ios", "validation.push.native_token"),
                     (NewToken(), "windows", "validation.push.native_platform") })
        {
            var refused = await employee.Client.PostAsJsonAsync($"{Api}/native", new { token = bad, platform });
            Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
            Assert.Contains(key, await refused.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        }
        Assert.Equal(1, await NativeAsync(t, employee.Id));
    }

    [Fact]
    public async Task A_removed_app_is_dropped_at_once_and_web_and_native_share_the_device_limit()
    {
        var t = await TenantAsync();
        var employee = t["employee"];
        var gone = NewToken();
        var kept = NewToken();
        await RegisterAsync(employee, gone, "ios");
        await RegisterAsync(employee, kept);

        Fcm.Status[gone] = HttpStatusCode.NotFound;
        await SendAsync(t, NotificationCategories.Mention, employee.Id);
        await DeliverAsync(t);
        Assert.Single(Fcm.To(kept));
        Assert.Equal(1, await NativeAsync(t, employee.Id));

        // The messaging service being down is tried again later, not dropped.
        Fcm.Status[kept] = HttpStatusCode.ServiceUnavailable;
        await SendAsync(t, NotificationCategories.Mention, employee.Id);
        await DeliverAsync(t);
        Assert.Equal(1, await world.Database.CountInTenantAsync(t.Id,
            "select count(*) from notifications.delivery where channel = 'push' and status = 'pending' and attempts = 1"));
        Fcm.Status.TryRemove(kept, out _);

        // Ten devices at most, web and native together, the oldest dropped first.
        for (var i = 0; i < 9; i++)
        {
            await RegisterAsync(employee, NewToken());
        }
        using var key = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var subscription = new
        {
            endpoint = "https://fcm.googleapis.com/fcm/send/" + Guid.NewGuid().ToString("N"),
            keys = new { p256dh = Base64Url.Encode(WebPushClient.Uncompressed(key.ExportParameters(false).Q)), auth = Base64Url.Encode(RandomNumberGenerator.GetBytes(16)) },
            platform = "windows",
        };
        var web = await employee.Client.PostAsJsonAsync($"{Api}/subscriptions", subscription);
        Assert.Equal(10, (await web.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("devices").GetInt32());
        Assert.Equal(9, await NativeAsync(t, employee.Id));
        Assert.Equal(0, await world.Database.CountInTenantAsync(t.Id, "select count(*) from notifications.native_push_device where token = @k", ("k", kept)));

        // Turning push off in the app removes only that device of this person.
        var last = await world.Database.CountInTenantAsync(t.Id, "select count(*) from notifications.native_push_device");
        Assert.Equal(9, last);
        Assert.Equal(10, (await (await employee.Client.PostAsJsonAsync($"{Api}/native:remove", new { token = NewToken() }))
            .Content.ReadFromJsonAsync<JsonElement>()).GetProperty("devices").GetInt32());
    }

    [Fact]
    public async Task Sign_out_removes_the_device_the_feature_decides_and_tenants_stay_apart()
    {
        var t = await TenantAsync();
        var other = await TenantAsync();
        var token = NewToken();
        await RegisterAsync(t["employee"], token);

        // The other tenant's person registers the same token: it is their own row, and removing it leaves the first.
        await RegisterAsync(other["employee"], token);
        await other["employee"].Client.PostAsJsonAsync($"{Api}/native:remove", new { token });
        Assert.Equal(0, await NativeAsync(other, other["employee"].Id));
        Assert.Equal(1, await NativeAsync(t, t["employee"].Id));

        Assert.True((await t["employee"].Client.PostAsync("/api/v1/auth/signout", null)).IsSuccessStatusCode);
        await DispatchAsync();
        Assert.Equal(0, await NativeAsync(t, t["employee"].Id));

        // Without the native shell feature the app cannot register, and the status says so; Web Push still works there.
        var plain = await TenantAsync("workplace");
        var status = await plain["employee"].Client.GetFromJsonAsync<JsonElement>(Api);
        Assert.False(status.GetProperty("nativeAvailable").GetBoolean());
        Assert.True(status.GetProperty("available").GetBoolean());
        var refused = await plain["employee"].Client.PostAsJsonAsync($"{Api}/native", new { token = NewToken(), platform = "android" });
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.Contains("entitlement.not_in_package", await refused.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        Assert.Equal(HttpStatusCode.Unauthorized, (await world.Factory.TenantClient(t.Key).PostAsJsonAsync($"{Api}/native", new { token, platform = "ios" })).StatusCode);
    }
}
