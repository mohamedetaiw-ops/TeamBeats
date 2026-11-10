using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TeamBeats.Audit.Contracts;
using TeamBeats.Modules.Integrations.Contracts;
using TeamBeats.Modules.Integrations.Runtime;
using TeamBeats.Modules.Integrations.Webhooks;
using TeamBeats.Modules.Services.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Primitives;
using TeamBeats.Testing;
using Xunit;

namespace TeamBeats.Integration.Tests;

/// <summary>
/// Spec 017 (R2) T011 and T013: outbound webhooks signed with HMAC SHA-256 over timestamp and body, with only the
/// selected fields, retried and logged as runs; inbound webhooks checked for signature, timestamp window and repeated
/// delivery ID before they become a product event or process message, with every refusal counted; secret rotation that
/// stops the old secret at once unless an overlap of at most 24 hours is set; the package and the tenant boundary.
/// External systems are the fake REST hosts of the test factory; secrets are fictional.
/// </summary>
[Collection(WorldCollection.Name)]
public sealed class WebhookTests(TenantWorld world)
{
    private const string Base = "/api/v1/integrations/pack-connections";

    private FakeRestApi Rest => world.Factory.Rest;

    private async Task<TestTenant> TenantAsync(string package = "workplace_plus") =>
        await world.CreateTenantAsync("hook-" + Guid.NewGuid().ToString("N")[..8], package);

    private static string Host() => $"hooks-{Guid.NewGuid():N}"[..20] + ".rest.test";

    private string Secret(TestTenant t, string name, string value)
    {
        var reference = SecretReferences.For(t.Id, name);
        world.Factory.Secrets.Set(reference, value);
        return reference;
    }

    private static async Task AllowAsync(TestTenant t, string host) =>
        Assert.Equal(HttpStatusCode.OK, (await t["admin"].Client.PostAsJsonAsync("/api/v1/integrations/allowlist", new { host })).StatusCode);

    private static object Outbound(string host, string secretRef) => new
    {
        url = $"https://{host}/teambeats",
        auth = new { type = "hmac_sha256", secretRef },
        events = new[] { new { type = "RequestCompleted", fields = new[] { "requestId", "status" } } },
    };

    private static object Inbound(string secretRef, string target = "event", string? correlationPath = null, int? overlapHours = null) => new
    {
        auth = new { type = "hmac_sha256", secretRef, overlapHours },
        inbound = new
        {
            target,
            name = "permits.approved",
            correlationPath,
            fields = new Dictionary<string, string> { ["permitId"] = "$.permit.id", ["status"] = "$.permit.status" },
            windowSeconds = 300,
        },
    };

    private static async Task<Guid> CreateAsync(TestTenant t, object settings, bool enable = true)
    {
        var response = await t["admin"].Client.PostAsJsonAsync(Base, new { kind = "webhook", displayName = "Permits webhook", settings });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var id = (await response.ReadAsync<JsonElement>()).GetProperty("id").GetGuid();
        if (enable)
        {
            Assert.Equal(HttpStatusCode.OK, (await t["admin"].Client.PostAsync($"{Base}/{id}:enable", null)).StatusCode);
        }
        return id;
    }

    private Task PublishAsync(TestTenant t, RequestCompleted e) =>
        world.Factory.InScopeAsync(t.Id, async sp =>
        {
            await ActivatorUtilities.CreateInstance<WebhookFanout<RequestCompleted>>(sp).HandleAsync(e, CancellationToken.None);
            await sp.GetRequiredService<IDbSession>().CommitAsync();
            return 0;
        });

    private Task<int> RunDueAsync(TestTenant t) =>
        world.Factory.InScopeAsync(t.Id, async sp =>
        {
            var n = await sp.GetRequiredService<ConnectorJobs>().RunDueAsync(DateTimeOffset.UtcNow.AddHours(1), 50, CancellationToken.None);
            await sp.GetRequiredService<IDbSession>().CommitAsync();
            return n;
        });

    private Task<HttpResponseMessage> DeliverAsync(TestTenant t, Guid connection, string body, string secret, string deliveryId, long? timestamp = null,
        string? signature = null)
    {
        var ts = timestamp ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var request = new HttpRequestMessage(HttpMethod.Post, InboundWebhooks.PathFor(connection))
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        request.Headers.TryAddWithoutValidation(WebhookSignature.TimestampHeader, ts.ToString(CultureInfo.InvariantCulture));
        request.Headers.TryAddWithoutValidation(WebhookSignature.DeliveryHeader, deliveryId);
        request.Headers.TryAddWithoutValidation(WebhookSignature.SignatureHeader, signature ?? WebhookSignature.Compute(secret, ts, body));
        return world.Factory.TenantClient(t.Key).SendAsync(request);
    }

    private async Task<JsonElement> CountsAsync(TestTenant t, Guid id)
    {
        var response = await t["admin"].Client.GetAsync($"{Base}/{id}/inbound");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.ReadAsync<JsonElement>();
    }

    private const string PermitBody = """{"permit":{"id":"P-1042","status":"approved","holder":"Not mapped"},"extra":true}""";

    [Fact]
    public async Task An_event_is_sent_signed_with_only_the_selected_fields_and_logged_as_a_run()
    {
        var t = await TenantAsync();
        var host = Host();
        await AllowAsync(t, host);
        Rest.On(host, _ => new HttpResponseMessage(HttpStatusCode.NoContent));
        var id = await CreateAsync(t, Outbound(host, Secret(t, "hook-out", "fictional-signing-value")));

        var request = Guid.NewGuid();
        await PublishAsync(t, new RequestCompleted(request, Guid.NewGuid(), "completed"));
        Assert.Equal(1, await RunDueAsync(t));

        var sent = Assert.Single(Rest.SentTo(host));
        Assert.Equal("POST", sent.Method);
        Assert.Equal("/teambeats", sent.Uri.AbsolutePath);
        var ts = long.Parse(sent.Headers[WebhookSignature.TimestampHeader], CultureInfo.InvariantCulture);
        Assert.Equal(WebhookSignature.Compute("fictional-signing-value", ts, sent.Body!), sent.Headers[WebhookSignature.SignatureHeader]);
        Assert.Equal("RequestCompleted", sent.Headers[WebhookSignature.EventHeader]);
        using var body = JsonDocument.Parse(sent.Body!);
        Assert.Equal(sent.Headers[WebhookSignature.DeliveryHeader], body.RootElement.GetProperty("id").GetString());
        var data = body.RootElement.GetProperty("data");
        Assert.Equal(request, data.GetProperty("requestId").GetGuid());
        Assert.Equal("completed", data.GetProperty("status").GetString());
        Assert.False(data.TryGetProperty("serviceId", out _));

        Assert.Equal("succeeded", await world.Database.TenantScalarAsync<string>(t.Id,
            "select status from integrations.run where connection_id = @id and action = 'deliver'", ("id", id)));
        Assert.Equal(0L, await world.Database.TenantScalarAsync<long>(t.Id,
            "select count(*) from integrations.run where connection_id = @id and input is not null", ("id", id)));
    }

    [Fact]
    public async Task A_failed_delivery_is_retried_with_the_same_delivery_id_and_a_turned_off_webhook_gets_nothing()
    {
        var t = await TenantAsync();
        var host = Host();
        await AllowAsync(t, host);
        var calls = 0;
        Rest.On(host, _ => ++calls == 1 ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) : new HttpResponseMessage(HttpStatusCode.OK));
        var id = await CreateAsync(t, Outbound(host, Secret(t, "hook-retry", "fictional-retry-value")));

        await PublishAsync(t, new RequestCompleted(Guid.NewGuid(), Guid.NewGuid(), "completed"));
        await RunDueAsync(t);
        Assert.Equal("retrying", await world.Database.TenantScalarAsync<string>(t.Id, "select status from integrations.run where connection_id = @id", ("id", id)));
        await RunDueAsync(t);
        var sent = Rest.SentTo(host);
        Assert.Equal(2, sent.Count);
        Assert.Equal(sent[0].Headers[WebhookSignature.DeliveryHeader], sent[1].Headers[WebhookSignature.DeliveryHeader]);
        Assert.Equal("succeeded", await world.Database.TenantScalarAsync<string>(t.Id, "select status from integrations.run where connection_id = @id", ("id", id)));

        Assert.Equal(HttpStatusCode.OK, (await t["admin"].Client.PostAsync($"{Base}/{id}:disable", null)).StatusCode);
        await PublishAsync(t, new RequestCompleted(Guid.NewGuid(), Guid.NewGuid(), "completed"));
        Assert.Equal(1L, await world.Database.TenantScalarAsync<long>(t.Id, "select count(*) from integrations.run where connection_id = @id", ("id", id)));
    }

    [Fact]
    public async Task A_signed_delivery_becomes_an_event_with_only_the_declared_fields_and_refusals_are_counted()
    {
        var t = await TenantAsync();
        var id = await CreateAsync(t, Inbound(Secret(t, "hook-in", "fictional-inbound-value")));

        var ok = await DeliverAsync(t, id, PermitBody, "fictional-inbound-value", "d-1");
        Assert.Equal(HttpStatusCode.Accepted, ok.StatusCode);
        var payload = await world.Database.TenantScalarAsync<string>(t.Id,
            "select payload::text from messaging.outbox where message_type = @m and tenant_id = @t", ("m", nameof(InboundWebhookReceived)), ("t", t.Id));
        using (var doc = JsonDocument.Parse(payload!))
        {
            var e = doc.RootElement;
            Assert.Equal("event", e.GetProperty("target").GetString());
            Assert.Equal("permits.approved", e.GetProperty("name").GetString());
            Assert.Equal("d-1", e.GetProperty("deliveryId").GetString());
            var fields = e.GetProperty("fields");
            Assert.Equal("P-1042", fields.GetProperty("permitId").GetString());
            Assert.Equal("approved", fields.GetProperty("status").GetString());
            Assert.False(fields.TryGetProperty("holder", out _));
            Assert.False(fields.TryGetProperty("extra", out _));
        }

        Assert.Equal(HttpStatusCode.Conflict, (await DeliverAsync(t, id, PermitBody, "fictional-inbound-value", "d-1")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await DeliverAsync(t, id, PermitBody, "wrong-value", "d-2")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await DeliverAsync(t, id, PermitBody, "fictional-inbound-value", "d-3",
            timestamp: DateTimeOffset.UtcNow.AddMinutes(-6).ToUnixTimeSeconds())).StatusCode);
        var tampered = await DeliverAsync(t, id, PermitBody.Replace("approved", "rejected"), "fictional-inbound-value", "d-4",
            signature: WebhookSignature.Compute("fictional-inbound-value", DateTimeOffset.UtcNow.ToUnixTimeSeconds(), PermitBody));
        Assert.Equal(HttpStatusCode.Unauthorized, tampered.StatusCode);

        var counts = await CountsAsync(t, id);
        Assert.Equal(1, counts.GetProperty("accepted").GetInt64());
        Assert.Equal(1, counts.GetProperty("refusedDuplicate").GetInt64());
        Assert.Equal(2, counts.GetProperty("refusedSignature").GetInt64());
        Assert.Equal(1, counts.GetProperty("refusedWindow").GetInt64());
        Assert.Equal(InboundWebhooks.PathFor(id), counts.GetProperty("path").GetString());
        Assert.Equal(1L, await world.Database.OutboxCountAsync(nameof(InboundWebhookReceived), t.Id));
    }

    [Fact]
    public async Task A_process_message_needs_its_correlation_key_and_a_body_over_one_megabyte_is_refused()
    {
        var t = await TenantAsync();
        var id = await CreateAsync(t, Inbound(Secret(t, "hook-msg", "fictional-message-value"), "message", "$.permit.id"));

        Assert.Equal(HttpStatusCode.Accepted, (await DeliverAsync(t, id, PermitBody, "fictional-message-value", "m-1")).StatusCode);
        var payload = await world.Database.TenantScalarAsync<string>(t.Id,
            "select payload::text from messaging.outbox where message_type = @m and tenant_id = @t", ("m", nameof(InboundWebhookReceived)), ("t", t.Id));
        using (var doc = JsonDocument.Parse(payload!))
        {
            Assert.Equal("message", doc.RootElement.GetProperty("target").GetString());
            Assert.Equal("P-1042", doc.RootElement.GetProperty("correlationKey").GetString());
        }
        Assert.Equal(HttpStatusCode.BadRequest, (await DeliverAsync(t, id, """{"permit":{}}""", "fictional-message-value", "m-2")).StatusCode);
        var large = "{\"pad\":\"" + new string('x', InboundWebhooks.MaxBodyBytes) + "\"}";
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, (await DeliverAsync(t, id, large, "fictional-message-value", "m-3")).StatusCode);
    }

    [Fact]
    public async Task A_rotated_secret_stops_the_old_one_at_once_unless_an_overlap_is_set()
    {
        var t = await TenantAsync();
        var first = Secret(t, "hook-rot-1", "fictional-first-value");
        var second = Secret(t, "hook-rot-2", "fictional-second-value");
        var third = Secret(t, "hook-rot-3", "fictional-third-value");
        var id = await CreateAsync(t, Inbound(first));

        var patch = await t["admin"].Client.PatchAsJsonAsync($"{Base}/{id}", new { settings = Inbound(second, overlapHours: 2) });
        Assert.Equal(HttpStatusCode.OK, patch.StatusCode);
        var auth = (await patch.ReadAsync<JsonElement>()).GetProperty("settings").GetProperty("auth");
        Assert.Equal(first, auth.GetProperty("previousSecretRef").GetString());
        Assert.False(auth.TryGetProperty("overlapHours", out _));
        Assert.Equal(HttpStatusCode.Accepted, (await DeliverAsync(t, id, PermitBody, "fictional-first-value", "r-1")).StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, (await DeliverAsync(t, id, PermitBody, "fictional-second-value", "r-2")).StatusCode);
        Assert.Equal(1, await world.Database.AuditCountAsync(t.Id, AuditActions.WebhookSecretRotated, id.ToString()));

        // A change that keeps the secret keeps the overlap; a new secret without overlap drops both old ones at once.
        Assert.Equal(HttpStatusCode.OK, (await t["admin"].Client.PatchAsJsonAsync($"{Base}/{id}", new { displayName = "Permits in" })).StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, (await DeliverAsync(t, id, PermitBody, "fictional-first-value", "r-3")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await t["admin"].Client.PatchAsJsonAsync($"{Base}/{id}", new { settings = Inbound(third) })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await DeliverAsync(t, id, PermitBody, "fictional-first-value", "r-4")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await DeliverAsync(t, id, PermitBody, "fictional-second-value", "r-5")).StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, (await DeliverAsync(t, id, PermitBody, "fictional-third-value", "r-6")).StatusCode);
        Assert.Equal(2, await world.Database.AuditCountAsync(t.Id, AuditActions.WebhookSecretRotated, id.ToString()));

        var tooLong = await t["admin"].Client.PatchAsJsonAsync($"{Base}/{id}", new { settings = Inbound(first, overlapHours: 25) });
        Assert.Equal(HttpStatusCode.BadRequest, tooLong.StatusCode);
    }

    [Fact]
    public async Task During_an_overlap_an_outbound_delivery_carries_a_signature_for_each_valid_secret()
    {
        var t = await TenantAsync();
        var host = Host();
        await AllowAsync(t, host);
        Rest.On(host, _ => new HttpResponseMessage(HttpStatusCode.OK));
        var id = await CreateAsync(t, Outbound(host, Secret(t, "hook-o1", "fictional-old-value")));
        var next = new
        {
            url = $"https://{host}/teambeats",
            auth = new { type = "hmac_sha256", secretRef = Secret(t, "hook-o2", "fictional-new-value"), overlapHours = 1 },
            events = new[] { new { type = "RequestCompleted", fields = new[] { "requestId" } } },
        };
        Assert.Equal(HttpStatusCode.OK, (await t["admin"].Client.PatchAsJsonAsync($"{Base}/{id}", new { settings = next })).StatusCode);

        await PublishAsync(t, new RequestCompleted(Guid.NewGuid(), Guid.NewGuid(), "completed"));
        await RunDueAsync(t);
        var sent = Assert.Single(Rest.SentTo(host));
        var ts = long.Parse(sent.Headers[WebhookSignature.TimestampHeader], CultureInfo.InvariantCulture);
        Assert.True(WebhookSignature.Matches(sent.Headers[WebhookSignature.SignatureHeader], ["fictional-new-value"], ts, sent.Body!));
        Assert.True(WebhookSignature.Matches(sent.Headers[WebhookSignature.SignatureHeader], ["fictional-old-value"], ts, sent.Body!));
    }

    [Fact]
    public async Task Settings_refuse_undeclared_fields_hosts_off_the_allowlist_and_secret_values()
    {
        var t = await TenantAsync();
        var host = Host();
        var secretRef = Secret(t, "hook-v", "fictional-check-value");
        async Task<HttpStatusCode> TryAsync(object settings) =>
            (await t["admin"].Client.PostAsJsonAsync(Base, new { kind = "webhook", displayName = "Check", settings })).StatusCode;

        Assert.Equal(HttpStatusCode.BadRequest, await TryAsync(Outbound(host, secretRef)));
        await AllowAsync(t, host);
        Assert.Equal(HttpStatusCode.BadRequest, await TryAsync(new
        {
            url = $"https://{host}/x",
            auth = new { type = "hmac_sha256", secretRef },
            events = new[] { new { type = "RequestCompleted", fields = new[] { "requestId", "submittedBy" } } },
        }));
        Assert.Equal(HttpStatusCode.BadRequest, await TryAsync(new
        {
            url = $"https://{host}/x",
            auth = new { type = "hmac_sha256", secretRef },
            events = new[] { new { type = "SomethingElse", fields = new[] { "id" } } },
        }));
        Assert.Equal(HttpStatusCode.BadRequest, await TryAsync(new { url = $"https://{host}/x", auth = new { type = "hmac_sha256", secretRef = "fictional-raw-secret" } }));
        Assert.Equal(HttpStatusCode.BadRequest, await TryAsync(Inbound(secretRef, "message")));
        Assert.Equal(HttpStatusCode.BadRequest, await TryAsync(new { auth = new { type = "hmac_sha256", secretRef } }));
        Assert.Equal(HttpStatusCode.Created, await TryAsync(Outbound(host, secretRef)));
    }

    [Fact]
    public async Task Webhooks_need_the_package_and_an_inbound_url_answers_only_on_its_own_tenant_host()
    {
        var a = await TenantAsync();
        var b = await TenantAsync();
        var plain = await TenantAsync("workplace");
        var id = await CreateAsync(a, Inbound(Secret(a, "hook-x", "fictional-cross-value")));

        Assert.Equal(HttpStatusCode.NotFound, (await DeliverAsync(b, id, PermitBody, "fictional-cross-value", "x-1")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await b["admin"].Client.GetAsync($"{Base}/{id}/inbound")).StatusCode);
        Assert.Equal(0L, await world.Database.TenantScalarAsync<long>(b.Id, "select count(*) from integrations.webhook_receipt"));
        Assert.Equal(HttpStatusCode.Accepted, (await DeliverAsync(a, id, PermitBody, "fictional-cross-value", "x-1")).StatusCode);

        var refused = await plain["admin"].Client.PostAsJsonAsync(Base, new { kind = "webhook", displayName = "No", settings = Inbound(Secret(plain, "hook-p", "v")) });
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);

        // Turned off: the URL is not in use, and nothing is counted.
        Assert.Equal(HttpStatusCode.OK, (await a["admin"].Client.PostAsync($"{Base}/{id}:disable", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await DeliverAsync(a, id, PermitBody, "fictional-cross-value", "x-2")).StatusCode);
        Assert.Equal(1, (await CountsAsync(a, id)).GetProperty("accepted").GetInt64());
    }

    [Fact]
    public async Task The_event_list_offers_each_event_with_its_fields_to_administrators_only()
    {
        var t = await TenantAsync();
        var response = await t["admin"].Client.GetAsync("/api/v1/integrations/webhook-events");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var list = await response.ReadAsync<JsonElement>();
        var completed = list.EnumerateArray().Single(e => e.GetProperty("type").GetString() == "RequestCompleted");
        Assert.Equal(["requestId", "serviceId", "status"], completed.GetProperty("fields").EnumerateArray().Select(f => f.GetString()!).ToArray());
        Assert.DoesNotContain(list.EnumerateArray().SelectMany(e => e.GetProperty("fields").EnumerateArray()), f => f.GetString() == "completedBy");

        var employee = await world.SeedUserAsync(t, "employee", "employee");
        Assert.Equal(HttpStatusCode.Forbidden, (await employee.Client.GetAsync("/api/v1/integrations/webhook-events")).StatusCode);
    }
}
