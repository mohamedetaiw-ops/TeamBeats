using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TeamBeats.Modules.Notifications;
using TeamBeats.Modules.Notifications.Contracts;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Primitives;
using TeamBeats.Testing;
using Xunit;

namespace TeamBeats.Integration.Tests;

/// <summary>
/// Spec 015 US6 scenario 2 (R2, FR-007, FR-020): a mandatory message reaches a person with neither email nor the app by
/// one SMS to the mobile number in the directory, through the tenant's SMS gateway connection. Only mandatory messages,
/// at most one SMS per message and person, counted against the monthly limit, and the number never lands in run history.
/// The gateway is a fake host of the test factory; every number is fictional.
/// </summary>
[Collection(WorldCollection.Name)]
public sealed class SmsNotificationTests(TenantWorld world)
{
    private const string Packs = "/api/v1/integrations/pack-connections";
    private const string Phone = "+971 50 000 0001";
    private const string E164 = "+971500000001";

    private FakeRestApi Rest => world.Factory.Rest;

    /// <summary>A small SMS gateway: messages it accepted, and how it answers next.</summary>
    private sealed class FakeGateway
    {
        public List<(JsonElement Body, string? Authorization)> Accepted { get; } = [];

        public HttpStatusCode Answer { get; set; } = HttpStatusCode.Created;

        public HttpResponseMessage Respond(FakeRestApi.Sent s)
        {
            if (s.Uri.AbsolutePath != "/v1/messages" || s.Method != "POST")
            {
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            }
            if (Answer != HttpStatusCode.Created)
            {
                return FakeRestApi.Json(Answer, "{\"error\":\"refused\"}");
            }
            lock (Accepted)
            {
                Accepted.Add((JsonDocument.Parse(s.Body!).RootElement.Clone(), s.Headers.GetValueOrDefault("Authorization")));
            }
            return FakeRestApi.Json(HttpStatusCode.Created, JsonSerializer.Serialize(new { id = "msg-" + Accepted.Count }));
        }
    }

    private sealed record Setup(TestTenant T, Guid Connection, FakeGateway Gateway);

    private static string Host() => $"sms-{Guid.NewGuid():N}"[..20] + ".rest.test";

    private async Task<TestTenant> TenantAsync(string package)
    {
        var t = await world.CreateTenantAsync("sm-" + Guid.NewGuid().ToString("N")[..8], package);
        await world.SeedUserAsync(t, "sara", MembershipTypes.Employee);
        return t;
    }

    /// <summary>Sara works on the floor: no work email, and the directory holds her mobile number.</summary>
    private Task FrontlineAsync(TestTenant t, string name, string? phone = Phone) =>
        world.Database.OwnerExecInTenantAsync(t.Id,
            """
            update identity.user_account set email = '' where id = @u;
            insert into identity.directory_user (tenant_id, connection_id, directory_id, account_enabled, attributes)
              values (@t, @c, @d, true, jsonb_build_object('mobilePhone', @p::text));
            insert into identity.directory_user_link (tenant_id, connection_id, directory_id, user_id) values (@t, @c, @d, @u);
            """,
            ("t", t.Id), ("u", t[name].Id), ("c", Guid.NewGuid()), ("d", Guid.NewGuid().ToString("D")), ("p", (object?)phone ?? DBNull.Value));

    private async Task<Setup> SetupAsync()
    {
        var t = await TenantAsync("enterprise_gov");
        await FrontlineAsync(t, "sara");
        var host = Host();
        var gateway = new FakeGateway();
        Rest.On(host, gateway.Respond);
        var admin = t["admin"].Client;
        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsJsonAsync("/api/v1/integrations/allowlist", new { host })).StatusCode);
        world.Factory.Secrets.Set(SecretReferences.For(t.Id, "sms-key"), "fictional-sms-key");

        // A sender that is neither a short name nor an international number is refused.
        var bad = await admin.PostAsJsonAsync(Packs, new
        {
            kind = "sms", displayName = "Gateway", settings = new { baseUrl = $"https://{host}/v1", apiKeyRef = SecretReferences.For(t.Id, "sms-key"), sender = "Acme Facilities" },
        });
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        Assert.Contains("sender", await bad.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        var created = await admin.PostAsJsonAsync(Packs, new
        {
            kind = "sms", displayName = "Gateway", settings = new { baseUrl = $"https://{host}/v1", apiKeyRef = SecretReferences.For(t.Id, "sms-key"), sender = "Acme" },
        });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var id = (await created.ReadAsync<JsonElement>()).GetProperty("id").GetGuid();
        var test = await (await admin.PostAsync($"{Packs}/{id}:test", null)).ReadAsync<JsonElement>();
        Assert.Equal("ok", test.GetProperty("status").GetString());
        Assert.Empty(gateway.Accepted);
        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsync($"{Packs}/{id}:enable", null)).StatusCode);
        return new Setup(t, id, gateway);
    }

    private Task SendAsync(TestTenant t, string category, Guid user) =>
        world.Factory.InScopeAsync(t.Id, async sp =>
        {
            await sp.GetRequiredService<INotificationSender>().SendAsync(new NotificationRequest([user], category, "system.sample", "/p/home",
                new Dictionary<string, string> { ["label"] = "HR" }));
            await sp.GetRequiredService<IDbSession>().CommitAsync();
            return 0;
        });

    private Task<int> DeliverAsync(TestTenant t, DateTimeOffset? at = null) =>
        world.Factory.InScopeAsync(t.Id, async sp =>
        {
            var count = await sp.GetRequiredService<SmsDelivery>().DeliverDueAsync(at ?? DateTimeOffset.UtcNow, 100, CancellationToken.None);
            await sp.GetRequiredService<IDbSession>().CommitAsync();
            return count;
        });

    private Task<long> DeliveriesAsync(TestTenant t, string status, string? error = null) =>
        world.Database.CountInTenantAsync(t.Id,
            "select count(*) from notifications.delivery where channel = 'sms' and status = @s and (@e::text is null or last_error = @e)",
            ("s", status), ("e", (object?)error ?? DBNull.Value));

    private static string Period => DateTime.UtcNow.ToString("yyyy-MM", CultureInfo.InvariantCulture);

    private async Task<long> UsedAsync(TestTenant t) =>
        await world.Database.TenantScalarAsync<long?>(t.Id,
            "select used from entitlements.usage_counter where capability_key = 'notifications.sms_monthly_messages' and period = @p", ("p", Period)) ?? 0;

    [Fact]
    public async Task A_mandatory_message_reaches_a_person_without_email_or_app_by_one_SMS_with_a_link()
    {
        var s = await SetupAsync();
        var sara = s.T["sara"];

        await SendAsync(s.T, NotificationCategories.CriticalAnnouncement, sara.Id);
        // An ordinary message never goes by SMS.
        await SendAsync(s.T, "announcement", sara.Id);
        Assert.Equal(1, await DeliveriesAsync(s.T, "pending"));
        Assert.Equal(1, await DeliverAsync(s.T));
        Assert.Equal(1, await DeliveriesAsync(s.T, "sent"));
        Assert.Equal(0, await DeliverAsync(s.T));

        var (body, authorization) = Assert.Single(s.Gateway.Accepted);
        Assert.Equal("Bearer fictional-sms-key", authorization);
        Assert.Equal(E164, body.GetProperty("to").GetString());
        Assert.Equal("Acme", body.GetProperty("from").GetString());
        var text = body.GetProperty("text").GetString()!;
        Assert.InRange(text.Length, 1, 160);
        Assert.EndsWith($"https://{s.T.Key}.{ApiFactory.BaseDomain}/p/home", text, StringComparison.Ordinal);
        Assert.StartsWith("notification-sms:", body.GetProperty("reference").GetString(), StringComparison.Ordinal);
        Assert.Equal(1, await UsedAsync(s.T));

        // The number and the text are in no run record.
        Assert.Equal(0, await world.Database.CountInTenantAsync(s.T.Id,
            "select count(*) from integrations.run where connection_id = @c and (coalesce(input::text, '') like '%971%' or coalesce(input::text, '') like '%/p/home%')", ("c", s.Connection)));
        Assert.Equal(1, await world.Database.CountInTenantAsync(s.T.Id,
            "select count(*) from integrations.run where connection_id = @c and action = 'send_sms' and status = 'succeeded'", ("c", s.Connection)));
    }

    [Fact]
    public async Task People_with_email_with_the_app_or_without_a_number_get_no_SMS()
    {
        var s = await SetupAsync();
        var sara = s.T["sara"];

        // Omar has a number in the directory but also a work email: email takes the message.
        await world.SeedUserAsync(s.T, "omar", MembershipTypes.Employee);
        await world.Database.OwnerExecInTenantAsync(s.T.Id,
            """
            insert into identity.directory_user (tenant_id, connection_id, directory_id, account_enabled, attributes)
              values (@t, @c, @d, true, '{"mobilePhone": "+971 50 000 0002"}');
            insert into identity.directory_user_link (tenant_id, connection_id, directory_id, user_id) values (@t, @c, @d, @u);
            """,
            ("t", s.T.Id), ("u", s.T["omar"].Id), ("c", Guid.NewGuid()), ("d", Guid.NewGuid().ToString("D")));
        await SendAsync(s.T, NotificationCategories.CriticalAnnouncement, s.T["omar"].Id);

        // Sara with the app on her phone: push takes it.
        await world.Database.OwnerExecInTenantAsync(s.T.Id,
            "insert into notifications.push_subscription (tenant_id, id, user_id, endpoint, p256dh, auth, platform, created_at) values (@t, @id, @u, 'https://push.test/sara', 'key', 'auth', 'android', now())",
            ("t", s.T.Id), ("id", Guid.NewGuid()), ("u", sara.Id));
        await SendAsync(s.T, NotificationCategories.SecurityNotice, sara.Id);
        await world.Database.OwnerExecInTenantAsync(s.T.Id, "delete from notifications.push_subscription where user_id = @u", ("u", sara.Id));

        // Sara without a number in the directory: nothing to send to.
        await world.Database.OwnerExecInTenantAsync(s.T.Id, "update identity.directory_user set attributes = '{}'");
        await SendAsync(s.T, NotificationCategories.AttestationDue, sara.Id);

        Assert.Equal(0, await DeliveriesAsync(s.T, "pending"));
        Assert.Equal(0, await DeliverAsync(s.T));
        Assert.Empty(s.Gateway.Accepted);
    }

    [Fact]
    public async Task A_refused_number_is_not_retried_and_does_not_count_and_a_failing_gateway_retries_once_counted()
    {
        var s = await SetupAsync();
        var sara = s.T["sara"];

        s.Gateway.Answer = HttpStatusCode.UnprocessableEntity;
        await SendAsync(s.T, NotificationCategories.CriticalAnnouncement, sara.Id);
        Assert.Equal(1, await DeliverAsync(s.T));
        Assert.Equal(1, await DeliveriesAsync(s.T, "dropped", "sms_number_refused"));
        Assert.Equal(0, await UsedAsync(s.T));

        // The gateway is down: the SMS waits and is sent once it is back, counted once, with the same reference.
        s.Gateway.Answer = HttpStatusCode.ServiceUnavailable;
        await SendAsync(s.T, NotificationCategories.SecurityNotice, sara.Id);
        Assert.Equal(1, await DeliverAsync(s.T));
        Assert.Equal(1, await DeliveriesAsync(s.T, "pending", "sms_unavailable"));
        Assert.Equal(1, await UsedAsync(s.T));
        Assert.Equal(0, await world.Database.CountInTenantAsync(s.T.Id,
            "select count(*) from integrations.run where connection_id = @c and coalesce(input::text, '') like '%971%'", ("c", s.Connection)));

        s.Gateway.Answer = HttpStatusCode.Created;
        Assert.Equal(1, await DeliverAsync(s.T, DateTimeOffset.UtcNow.AddMinutes(10)));
        Assert.Equal(1, await DeliveriesAsync(s.T, "sent"));
        Assert.Single(s.Gateway.Accepted);
        Assert.Equal(1, await UsedAsync(s.T));
    }

    [Fact]
    public async Task At_the_monthly_limit_no_SMS_is_sent_and_the_delivery_says_why()
    {
        var s = await SetupAsync();
        await world.Database.OwnerExecInTenantAsync(s.T.Id,
            "insert into entitlements.usage_counter (tenant_id, capability_key, period, used) values (@t, 'notifications.sms_monthly_messages', @p, 2000)",
            ("t", s.T.Id), ("p", Period));

        await SendAsync(s.T, NotificationCategories.CriticalAnnouncement, s.T["sara"].Id);
        Assert.Equal(1, await DeliverAsync(s.T));
        Assert.Equal(1, await DeliveriesAsync(s.T, "dropped", "sms_quota"));
        Assert.Empty(s.Gateway.Accepted);
        Assert.Equal(2000, await UsedAsync(s.T));
    }

    [Fact]
    public async Task SMS_stays_inside_its_tenant_and_its_package()
    {
        var s = await SetupAsync();

        // Another tenant with the same kind of person but no gateway gets no SMS, and the first tenant's gateway is not used.
        var other = await TenantAsync("enterprise_gov");
        await FrontlineAsync(other, "sara");
        await SendAsync(other, NotificationCategories.CriticalAnnouncement, other["sara"].Id);
        Assert.Equal(0, await DeliveriesAsync(other, "pending"));

        // A package without SMS: no SMS delivery, and the connection type cannot be added.
        var plus = await TenantAsync("workplace_plus");
        await FrontlineAsync(plus, "sara");
        await SendAsync(plus, NotificationCategories.CriticalAnnouncement, plus["sara"].Id);
        Assert.Equal(0, await DeliveriesAsync(plus, "pending"));
        var refused = await plus["admin"].Client.PostAsJsonAsync(Packs, new
        {
            kind = "sms", displayName = "Gateway", settings = new { baseUrl = "https://sms.rest.test/v1", apiKeyRef = SecretReferences.For(plus.Id, "sms-key"), sender = "Acme" },
        });
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);

        // Turning the gateway off stops SMS for messages already queued.
        await SendAsync(s.T, NotificationCategories.CriticalAnnouncement, s.T["sara"].Id);
        Assert.Equal(HttpStatusCode.OK, (await s.T["admin"].Client.PostAsync($"{Packs}/{s.Connection}:disable", null)).StatusCode);
        Assert.Equal(1, await DeliverAsync(s.T));
        Assert.Equal(1, await DeliveriesAsync(s.T, "dropped", "sms_off"));
        Assert.Empty(s.Gateway.Accepted);
        Assert.Equal(0, await UsedAsync(s.T));
    }
}
