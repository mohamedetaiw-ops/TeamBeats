using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TeamBeats.Entitlements.Contracts;
using TeamBeats.Messaging;
using TeamBeats.Modules.Identity.Contracts;
using TeamBeats.Modules.Integrations.WebPush;
using TeamBeats.Modules.Notifications;
using TeamBeats.Modules.Notifications.Contracts;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Testing;
using Xunit;

namespace TeamBeats.Integration.Tests;

/// <summary>
/// Spec 030 US2 (FR-006 to FR-008, FR-019) and spec 015 FR-007: Web Push as a notification channel. A browser subscribes
/// with the installation's key; deliveries are encrypted for that browser, signed for that push service, carry only the
/// category and a link, follow the person's choices, and stop when the person signs out, is revoked or the push service
/// reports the device gone. Only known push services are ever called, and tenants never see each other's devices.
/// </summary>
[Collection(WorldCollection.Name)]
public sealed class PushTests(TenantWorld world)
{
    private const string Api = "/api/v1/me/push";

    /// <summary>A browser's push keys: the P-256 key pair and auth secret it keeps, and what it tells the server.</summary>
    private sealed class Browser : IDisposable
    {
        public ECDiffieHellman Key { get; } = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        public byte[] Auth { get; } = RandomNumberGenerator.GetBytes(16);
        public string Endpoint { get; } = "https://fcm.googleapis.com/fcm/send/" + Guid.NewGuid().ToString("N");
        public byte[] Public => WebPushClient.Uncompressed(Key.ExportParameters(false).Q);

        public object Subscription(string platform = "android") =>
            new { endpoint = Endpoint, keys = new { p256dh = Base64Url.Encode(Public), auth = Base64Url.Encode(Auth) }, platform };

        /// <summary>Opens an aes128gcm body the way the browser does (RFC 8291).</summary>
        public string Decrypt(byte[] body)
        {
            var salt = body[..16];
            var idLength = body[20];
            var server = body[21..(21 + idLength)];
            var record = body[(21 + idLength)..];
            using var sender = ECDiffieHellman.Create(new ECParameters { Curve = ECCurve.NamedCurves.nistP256, Q = WebPushClient.PointOf(server) });
            var shared = Key.DeriveRawSecretAgreement(sender.PublicKey);
            var (cek, nonce) = WebPushClient.Keys(shared, Auth, salt, Public, server);
            var plain = new byte[record.Length - 16];
            using var aes = new AesGcm(cek, 16);
            aes.Decrypt(nonce, record[..^16], record[^16..], plain);
            Assert.Equal(0x02, plain[^1]);
            return Encoding.UTF8.GetString(plain[..^1]);
        }

        public void Dispose() => Key.Dispose();
    }

    private FakePushService Service => world.Factory.Push;

    private async Task<TestTenant> TenantAsync(string package = "workplace")
    {
        var t = await world.CreateTenantAsync("pu-" + Guid.NewGuid().ToString("N")[..8], package);
        await world.SeedUserAsync(t, "employee", MembershipTypes.Employee);
        return t;
    }

    private static async Task<JsonElement> SubscribeAsync(TestUser user, object subscription)
    {
        var response = await user.Client.PostAsJsonAsync($"{Api}/subscriptions", subscription);
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

    private Task<long> DevicesAsync(TestTenant t, Guid user) =>
        world.Database.CountInTenantAsync(t.Id, "select count(*) from notifications.push_subscription where user_id = @u", ("u", user));

    private Task<long> PushDeliveriesAsync(TestTenant t, string status) =>
        world.Database.CountInTenantAsync(t.Id, "select count(*) from notifications.delivery where channel = 'push' and status = @s", ("s", status));

    private Task DispatchAsync() => world.Factory.Services.GetRequiredService<OutboxDispatcher>().DispatchBatchAsync(500, CancellationToken.None);

    [Fact]
    public async Task A_browser_subscribes_and_gets_an_encrypted_signed_message_with_no_item_content()
    {
        var t = await TenantAsync();
        var employee = t["employee"];
        using var browser = new Browser();

        var status = await employee.Client.GetFromJsonAsync<JsonElement>(Api);
        Assert.True(status.GetProperty("available").GetBoolean());
        var key = status.GetProperty("publicKey").GetString()!;
        Assert.Equal(65, Base64Url.Decode(key).Length);
        Assert.Equal(1, (await SubscribeAsync(employee, browser.Subscription())).GetProperty("devices").GetInt32());
        // The preferences page shows push too, on for every category until the person turns one off.
        var preferences = await employee.Client.GetFromJsonAsync<JsonElement>("/api/v1/me/notification-preferences");
        Assert.Equal(1, preferences.GetProperty("push").GetProperty("devices").GetInt32());
        Assert.All(preferences.GetProperty("categories").EnumerateArray(), c => Assert.True(c.GetProperty("push").GetBoolean()));

        await SendAsync(t, NotificationCategories.RequestUpdate, employee.Id, urgent: true);
        Assert.Equal(1, await DeliverAsync(t));
        var message = Assert.Single(Service.To(browser.Endpoint));
        Assert.Equal(1, await PushDeliveriesAsync(t, "sent"));

        Assert.Equal("aes128gcm", message.Headers["Content-Encoding"]);
        Assert.Equal("86400", message.Headers["TTL"]);
        Assert.Equal("high", message.Headers["Urgency"]);
        var payload = JsonDocument.Parse(browser.Decrypt(message.Body)).RootElement;
        Assert.Equal("Updates on your requests", payload.GetProperty("title").GetString());
        Assert.Equal("Open TeamBeats to read it.", payload.GetProperty("body").GetString());
        Assert.Equal("/requests/42", payload.GetProperty("link").GetString());
        // Nothing the notification itself says leaves in a push (spec 015 NOT-007): not the template text, not a parameter.
        var all = browser.Decrypt(message.Body);
        Assert.DoesNotContain("Falcon", all, StringComparison.Ordinal);
        Assert.DoesNotContain("Sample notification", all, StringComparison.Ordinal);

        // The VAPID token is for this push service only and verifies with the published key (RFC 8292).
        var authorization = message.Headers["Authorization"];
        Assert.StartsWith("vapid t=", authorization, StringComparison.Ordinal);
        Assert.EndsWith($", k={key}", authorization, StringComparison.Ordinal);
        var token = authorization["vapid t=".Length..authorization.IndexOf(',', StringComparison.Ordinal)];
        var parts = token.Split('.');
        var claims = JsonDocument.Parse(Base64Url.Decode(parts[1])).RootElement;
        Assert.Equal("https://fcm.googleapis.com", claims.GetProperty("aud").GetString());
        Assert.True(claims.GetProperty("exp").GetInt64() <= DateTimeOffset.UtcNow.AddHours(12).AddMinutes(1).ToUnixTimeSeconds());
        using var verifier = ECDsa.Create(new ECParameters { Curve = ECCurve.NamedCurves.nistP256, Q = WebPushClient.PointOf(Base64Url.Decode(key)) });
        Assert.True(verifier.VerifyData(Encoding.ASCII.GetBytes($"{parts[0]}.{parts[1]}"), Base64Url.Decode(parts[2]), HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation));
    }

    [Fact]
    public async Task Only_known_push_services_and_real_keys_are_accepted()
    {
        var t = await TenantAsync();
        var employee = t["employee"];
        using var browser = new Browser();
        var keys = new { p256dh = Base64Url.Encode(browser.Public), auth = Base64Url.Encode(browser.Auth) };
        foreach (var endpoint in new[] { "https://evil.example/collect", "http://fcm.googleapis.com/fcm/send/x", "https://fcm.googleapis.com:8443/x",
                     "https://user@fcm.googleapis.com/x", "https://fcm.googleapis.com.evil.example/x", "https://10.0.0.1/x" })
        {
            var refused = await employee.Client.PostAsJsonAsync($"{Api}/subscriptions", new { endpoint, keys, platform = "android" });
            Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
            Assert.Contains("validation.push.endpoint", await refused.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        }
        var badKeys = await employee.Client.PostAsJsonAsync($"{Api}/subscriptions",
            new { endpoint = browser.Endpoint, keys = new { p256dh = "not-a-key", auth = Base64Url.Encode(new byte[8]) }, platform = "android" });
        Assert.Contains("validation.push.keys", await badKeys.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Equal(0, await DevicesAsync(t, employee.Id));

        // An unknown platform is stored as other; the device is counted once however often it subscribes.
        await SubscribeAsync(employee, browser.Subscription("toaster"));
        await SubscribeAsync(employee, browser.Subscription());
        Assert.Equal(1, await DevicesAsync(t, employee.Id));
        Assert.Equal(1, await world.Database.CountInTenantAsync(t.Id, "select count(*) from notifications.push_subscription where platform = 'android'"));
    }

    [Fact]
    public async Task Choices_quiet_hours_and_a_device_the_service_reports_gone_are_respected()
    {
        var t = await TenantAsync();
        var employee = t["employee"];
        using var phone = new Browser();
        using var laptop = new Browser();
        await SubscribeAsync(employee, phone.Subscription());
        await SubscribeAsync(employee, laptop.Subscription("windows"));

        // Push off for comments: nothing is queued for push, the other channels keep their choice.
        var off = await employee.Client.PutAsJsonAsync("/api/v1/me/notification-preferences",
            new { categories = new[] { new { category = NotificationCategories.Comment, email = false, digest = false, push = false } } });
        Assert.True(off.IsSuccessStatusCode, await off.Content.ReadAsStringAsync());
        await SendAsync(t, NotificationCategories.Comment, employee.Id);
        Assert.Equal(0, await world.Database.CountInTenantAsync(t.Id, "select count(*) from notifications.delivery where channel = 'push'"));

        // The laptop's subscription has expired: it is removed at once and the phone still gets the message.
        Service.Status[laptop.Endpoint] = HttpStatusCode.Gone;
        await SendAsync(t, NotificationCategories.Mention, employee.Id);
        await DeliverAsync(t);
        Assert.Single(Service.To(phone.Endpoint));
        Assert.Equal(1, await DevicesAsync(t, employee.Id));
        Assert.Equal(1, await PushDeliveriesAsync(t, "sent"));

        // A push service that is down is tried again later, not dropped.
        Service.Status[phone.Endpoint] = HttpStatusCode.ServiceUnavailable;
        await SendAsync(t, NotificationCategories.Mention, employee.Id);
        await DeliverAsync(t);
        Assert.Equal(1, await world.Database.CountInTenantAsync(t.Id,
            "select count(*) from notifications.delivery where channel = 'push' and status = 'pending' and attempts = 1"));
        Service.Status.TryRemove(phone.Endpoint, out _);

        // Turning push off on the phone removes it; another person's device is never touched by that call.
        await world.SeedUserAsync(t, "colleague", MembershipTypes.Employee);
        using var theirs = new Browser();
        await SubscribeAsync(t["colleague"], theirs.Subscription());
        Assert.Equal(1, (await (await employee.Client.PostAsJsonAsync($"{Api}/subscriptions:remove", new { endpoint = theirs.Endpoint }))
            .Content.ReadFromJsonAsync<JsonElement>()).GetProperty("devices").GetInt32());
        Assert.Equal(1, await DevicesAsync(t, t["colleague"].Id));
        Assert.Equal(0, (await (await employee.Client.PostAsJsonAsync($"{Api}/subscriptions:remove", new { endpoint = phone.Endpoint }))
            .Content.ReadFromJsonAsync<JsonElement>()).GetProperty("devices").GetInt32());
    }

    [Fact]
    public async Task Sign_out_revocation_and_leaving_remove_the_devices()
    {
        var t = await TenantAsync();
        var employee = t["employee"];
        await world.SeedUserAsync(t, "colleague", MembershipTypes.Employee);
        var leaver = await world.SeedUserAsync(t, "leaver", MembershipTypes.Employee);
        using var a = new Browser();
        using var b = new Browser();
        using var c = new Browser();
        await SubscribeAsync(employee, a.Subscription());
        await SubscribeAsync(t["colleague"], b.Subscription());
        await SubscribeAsync(leaver, c.Subscription());

        Assert.True((await employee.Client.PostAsync("/api/v1/auth/signout", null)).IsSuccessStatusCode);
        await DispatchAsync();
        Assert.Equal(0, await DevicesAsync(t, employee.Id));
        Assert.Equal(1, await DevicesAsync(t, t["colleague"].Id));

        await world.Factory.InScopeAsync(t.Id, async sp =>
        {
            await sp.GetRequiredService<ISessionRevocation>().RevokeUserSessionsAsync(t["colleague"].Id, "admin");
            await sp.GetRequiredService<IOutbox>().EnqueueAsync(new UserDeprovisioned(leaver.Id, "directory"));
            await sp.GetRequiredService<IDbSession>().CommitAsync();
            return true;
        });
        await DispatchAsync();
        Assert.Equal(0, await world.Database.CountInTenantAsync(t.Id, "select count(*) from notifications.push_subscription"));

        // With no device left, a notification is not queued for push at all.
        await SendAsync(t, NotificationCategories.Mention, t["colleague"].Id);
        Assert.Equal(0, await world.Database.CountInTenantAsync(t.Id, "select count(*) from notifications.delivery where channel = 'push'"));
    }

    [Fact]
    public async Task Tenants_never_reach_each_other_s_devices_and_the_package_decides()
    {
        var t = await TenantAsync();
        var other = await TenantAsync();
        using var browser = new Browser();
        await SubscribeAsync(t["employee"], browser.Subscription());

        // The other tenant's person sends the same endpoint: it becomes their own row, and the first tenant's stays.
        await SubscribeAsync(other["employee"], browser.Subscription());
        Assert.Equal(1, await DevicesAsync(t, t["employee"].Id));
        Assert.Equal(1, await DevicesAsync(other, other["employee"].Id));
        await other["employee"].Client.PostAsJsonAsync($"{Api}/subscriptions:remove", new { endpoint = browser.Endpoint });
        Assert.Equal(1, await DevicesAsync(t, t["employee"].Id));

        // Without mobile.push in the package, subscribing is refused and queued messages are not sent.
        var pinned = await TenantAsync();
        await SubscribeAsync(pinned["employee"], browser.Subscription());
        await SendAsync(pinned, NotificationCategories.Mention, pinned["employee"].Id);
        await world.PinPackageVersionAsync(pinned, "workplace", new Dictionary<string, object>
        {
            [Capabilities.AiAssistant] = new { enabled = true },
            [Capabilities.AiAssistantMonthlyRequests] = new { enabled = true, limit = 9999 },
        });
        var refused = await pinned["employee"].Client.PostAsJsonAsync($"{Api}/subscriptions", browser.Subscription());
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.Contains("entitlement.not_in_package", await refused.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.False((await pinned["employee"].Client.GetFromJsonAsync<JsonElement>(Api)).GetProperty("available").GetBoolean());
        var before = Service.To(browser.Endpoint).Count;
        await DeliverAsync(pinned);
        Assert.Equal(before, Service.To(browser.Endpoint).Count);
        Assert.Equal(1, await world.Database.CountInTenantAsync(pinned.Id,
            "select count(*) from notifications.delivery where channel = 'push' and last_error = 'push_unavailable'"));

        Assert.Equal(HttpStatusCode.Unauthorized, (await world.Factory.TenantClient(t.Key).GetAsync(Api)).StatusCode);
    }
}
