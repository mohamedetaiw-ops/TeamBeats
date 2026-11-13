using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TeamBeats.Audit.Contracts;
using TeamBeats.Modules.Audit;
using TeamBeats.Modules.Integrations.Contracts;
using TeamBeats.Modules.Integrations.Webhooks;
using TeamBeats.Modules.Security;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Primitives;
using TeamBeats.Testing;
using Xunit;

namespace TeamBeats.Integration.Tests;

/// <summary>
/// Spec 026 T018 (US4, FR-014, FR-018): audit and security events stream to the tenant's SIEM over signed HTTPS JSON or
/// syslog over TLS, in order, from each destination's place in the stream; an outage holds them back and they are sent
/// again with no gaps; a test event; secret rotation; plan, permission and tenant boundaries; audited configuration.
/// </summary>
[Collection(WorldCollection.Name)]
public sealed class SecuritySiemTests(TenantWorld world)
{
    private const string Siem = "/api/v1/security/siem";

    private sealed record Setup(TestTenant T, string Host, string SecretRef, string Secret);

    private async Task<Setup> FreshAsync(string package = "workplace_plus")
    {
        var t = await world.CreateTenantAsync("siem-" + Guid.NewGuid().ToString("N")[..8], package);
        await world.SeedUserAsync(t, "officer", MembershipTypes.Employee);
        await world.AssignAsync(t, "officer", "security_officer", "tenant", null);
        var host = $"siem-{Guid.NewGuid():N}"[..20] + ".example.test";
        await Ok(await t["admin"].Client.PostAsJsonAsync("/api/v1/integrations/allowlist", new { host }));
        var secretRef = SecretReferences.For(t.Id, "siem-" + Guid.NewGuid().ToString("N")[..6]);
        var secret = "fictional-siem-" + Guid.NewGuid().ToString("N")[..8];
        world.Factory.Secrets.Set(secretRef, secret);
        world.Factory.Rest.On(host, _ => new HttpResponseMessage(HttpStatusCode.Accepted));
        return new Setup(t, host, secretRef, secret);
    }

    private static async Task<JsonElement> Ok(HttpResponseMessage response, HttpStatusCode expected = HttpStatusCode.OK)
    {
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == expected, $"{(int)response.StatusCode}: {text}");
        return text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone();
    }

    private static async Task<JsonElement> CreateHttpsAsync(Setup s, int? historyDays = null) =>
        await Ok(await s.T["admin"].Client.PostAsJsonAsync(Siem, new
        {
            name = "Sentinel", transport = "https", url = $"https://{s.Host}/ingest", secretRef = s.SecretRef, historyDays,
        }), HttpStatusCode.Created);

    /// <summary>Seals the tenant's audit rows, as the audit sealer does every 5 seconds, then runs the stream once.</summary>
    private async Task<int> StreamAsync(TestTenant t)
    {
        await world.Factory.InScopeAsync(t.Id, async sp =>
        {
            await sp.GetRequiredService<AuditMaintenance>().SealAsync(10_000, CancellationToken.None);
            await sp.GetRequiredService<IDbSession>().CommitAsync();
            return 0;
        });
        return await world.Factory.InScopeAsync(t.Id, async sp =>
        {
            var sent = await sp.GetRequiredService<SiemStreamer>().RunAsync(CancellationToken.None);
            await sp.GetRequiredService<IDbSession>().CommitAsync();
            return sent;
        });
    }

    private static List<JsonElement> Events(IEnumerable<FakeRestApi.Sent> sent) =>
        sent.SelectMany(s => JsonDocument.Parse(s.Body!).RootElement.GetProperty("events").EnumerateArray().Select(e => e.Clone())).ToList();

    private static void AssertContiguous(IReadOnlyList<JsonElement> events)
    {
        var positions = events.Select(e => e.GetProperty("position").GetInt64()).ToList();
        Assert.Equal(Enumerable.Range(0, positions.Count).Select(i => positions[0] + i), positions);
    }

    [Fact]
    public async Task Events_stream_in_order_signed_with_the_tenants_secret_and_without_secrets()
    {
        var s = await FreshAsync();
        var created = await CreateHttpsAsync(s);
        Assert.Equal($"https://{s.Host}/ingest", created.GetProperty("target").GetString());

        // A few security events after the destination was made.
        await Ok(await s.T["admin"].Client.PutAsJsonAsync("/api/v1/security/policy", new { idleMinutes = 25 }));
        await Ok(await s.T["admin"].Client.PutAsJsonAsync("/api/v1/security/policy", new { idleMinutes = 20 }));
        Assert.True(await StreamAsync(s.T) >= 3);

        var sent = world.Factory.Rest.SentTo(s.Host);
        var request = Assert.Single(sent);
        var timestamp = long.Parse(request.Headers[WebhookSignature.TimestampHeader], System.Globalization.CultureInfo.InvariantCulture);
        Assert.True(WebhookSignature.Matches(request.Headers[WebhookSignature.SignatureHeader], [s.Secret], timestamp, request.Body!));
        Assert.DoesNotContain(s.Secret, request.Body!, StringComparison.Ordinal);
        var events = Events(sent);
        AssertContiguous(events);
        Assert.All(events, e => Assert.Equal(s.T.Id, e.GetProperty("tenant").GetGuid()));
        var actions = events.Select(e => e.GetProperty("action").GetString()).ToList();
        // The stream starts at the end of what was sealed when the destination was made, so the setup's own events come first.
        Assert.True(actions.IndexOf(AuditActions.SecuritySiemConfigured) < actions.IndexOf(AuditActions.SecurityPolicyChanged));
        Assert.Equal(2, actions.Count(a => a == AuditActions.SecurityPolicyChanged));
        var change = events.Last(e => e.GetProperty("action").GetString() == AuditActions.SecurityPolicyChanged);
        Assert.Equal("user", change.GetProperty("actor").GetProperty("type").GetString());
        Assert.Equal("success", change.GetProperty("outcome").GetString());

        // Nothing new: nothing sent. The page shows the destination caught up.
        Assert.Equal(0, await StreamAsync(s.T));
        Assert.Single(world.Factory.Rest.SentTo(s.Host));
        var listed = Assert.Single((await Ok(await s.T["officer"].Client.GetAsync(Siem))).GetProperty("destinations").EnumerateArray());
        Assert.Equal(listed.GetProperty("streamEnd").GetInt64(), listed.GetProperty("position").GetInt64());
        Assert.Equal(JsonValueKind.String, listed.GetProperty("lastSuccessAt").ValueKind);
    }

    [Fact]
    public async Task A_destination_that_is_down_gets_every_event_in_order_once_it_is_back()
    {
        var s = await FreshAsync();
        var id = (await CreateHttpsAsync(s)).GetProperty("id").GetGuid();
        var up = false;
        world.Factory.Rest.On(s.Host, _ => new HttpResponseMessage(up ? HttpStatusCode.OK : HttpStatusCode.ServiceUnavailable));

        await Ok(await s.T["admin"].Client.PutAsJsonAsync("/api/v1/security/policy", new { idleMinutes = 25 }));
        Assert.Equal(0, await StreamAsync(s.T));
        var failing = Assert.Single((await Ok(await s.T["admin"].Client.GetAsync(Siem))).GetProperty("destinations").EnumerateArray());
        Assert.Equal("server_error", failing.GetProperty("lastError").GetString());
        Assert.Equal(JsonValueKind.String, failing.GetProperty("failingSince").ValueKind);
        Assert.Equal(1, await world.Database.CountInTenantAsync(s.T.Id,
            "select count(*) from audit.audit_event where action = @a and target_id = @id", ("a", AuditActions.SecuritySiemDeliveryFailed), ("id", id.ToString())));

        // While it waits for its next attempt nothing is sent; more events pile up behind it.
        var attempts = world.Factory.Rest.SentTo(s.Host).Count;
        await Ok(await s.T["admin"].Client.PutAsJsonAsync("/api/v1/security/policy", new { idleMinutes = 20 }));
        Assert.Equal(0, await StreamAsync(s.T));
        Assert.Equal(attempts, world.Factory.Rest.SentTo(s.Host).Count);

        // A day of failures tells the security officers once.
        await world.Database.OwnerExecInTenantAsync(s.T.Id,
            "update security.siem_destination set next_attempt_at = null, failing_since = now() - interval '25 hours'");
        Assert.Equal(0, await StreamAsync(s.T));
        Assert.Equal(1, await world.Database.CountInTenantAsync(s.T.Id, "select count(*) from security.alert where kind = @k", ("k", AlertKinds.SiemFailing)));

        // Back up: everything held back goes out in order, with no gap, and the failure state clears.
        up = true;
        var before = world.Factory.Rest.SentTo(s.Host).Count;
        await world.Database.OwnerExecInTenantAsync(s.T.Id, "update security.siem_destination set next_attempt_at = null");
        Assert.True(await StreamAsync(s.T) > 0);
        var delivered = Events(world.Factory.Rest.SentTo(s.Host).Skip(before));
        AssertContiguous(delivered);
        var actions = delivered.Select(e => e.GetProperty("action").GetString()).ToList();
        Assert.Contains(AuditActions.SecuritySiemConfigured, actions);
        Assert.Contains(AuditActions.SecuritySiemDeliveryFailed, actions);
        Assert.Equal(2, actions.Count(a => a == AuditActions.SecurityPolicyChanged));
        var healthy = Assert.Single((await Ok(await s.T["admin"].Client.GetAsync(Siem))).GetProperty("destinations").EnumerateArray());
        Assert.Equal(JsonValueKind.Null, healthy.GetProperty("failingSince").ValueKind);
        Assert.Equal(healthy.GetProperty("streamEnd").GetInt64(), healthy.GetProperty("position").GetInt64());
    }

    [Fact]
    public async Task Syslog_over_tls_sends_one_rfc_5424_message_per_event()
    {
        var s = await FreshAsync();
        await Ok(await s.T["admin"].Client.PostAsJsonAsync(Siem, new { name = "Splunk", transport = "syslog", host = s.Host.ToUpperInvariant(), port = 6514 }),
            HttpStatusCode.Created);
        await Ok(await s.T["admin"].Client.PutAsJsonAsync("/api/v1/security/policy", new { idleMinutes = 25 }));
        Assert.Equal(400, (int)(await s.T["admin"].Client.PostAsJsonAsync(Siem, new { name = "Bad", transport = "syslog", host = "10.0.0.1", port = 6514 })).StatusCode);
        Assert.True(await StreamAsync(s.T) >= 2);

        var messages = world.Factory.Syslog.ReceivedBy(s.Host);
        Assert.True(messages.Count >= 2);
        Assert.All(messages, m => Assert.Matches(@"^<1(08|10)>1 \d{4}-\d\d-\d\dT[^ ]+ teambeats teambeats - [!-~]{1,32} - \{", m));
        var policy = messages.Single(m => m.Contains($" {AuditActions.SecurityPolicyChanged} - ", StringComparison.Ordinal));
        var json = JsonDocument.Parse(policy[(policy.IndexOf(" - {", StringComparison.Ordinal) + 3)..]).RootElement;
        Assert.Equal(s.T.Id, json.GetProperty("tenant").GetGuid());

        // A receiver that is down keeps the events for later.
        world.Factory.Syslog.SetDown(s.Host, true);
        await Ok(await s.T["admin"].Client.PutAsJsonAsync("/api/v1/security/policy", new { idleMinutes = 20 }));
        Assert.Equal(0, await StreamAsync(s.T));
        world.Factory.Syslog.SetDown(s.Host, false);
        await world.Database.OwnerExecInTenantAsync(s.T.Id, "update security.siem_destination set next_attempt_at = null");
        Assert.True(await StreamAsync(s.T) > 0);
        Assert.Equal(2, world.Factory.Syslog.ReceivedBy(s.Host).Count(m => m.Contains($" {AuditActions.SecurityPolicyChanged} - ", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task A_test_event_reports_the_result_and_a_new_secret_replaces_the_old_one_at_once()
    {
        var s = await FreshAsync();
        var created = await CreateHttpsAsync(s);
        var id = created.GetProperty("id").GetGuid();
        var test = await Ok(await s.T["admin"].Client.PostAsync($"{Siem}/{id}:test", null));
        Assert.True(test.GetProperty("delivered").GetBoolean());
        var sentTest = Events([world.Factory.Rest.SentTo(s.Host).Last()]).Single();
        Assert.Equal(SiemEvents.TestAction, sentTest.GetProperty("action").GetString());

        world.Factory.Rest.On(s.Host, _ => new HttpResponseMessage(HttpStatusCode.Forbidden));
        var refused = await Ok(await s.T["admin"].Client.PostAsync($"{Siem}/{id}:test", null));
        Assert.False(refused.GetProperty("delivered").GetBoolean());
        Assert.Equal("credential_refused", refused.GetProperty("errorClass").GetString());
        world.Factory.Rest.On(s.Host, _ => new HttpResponseMessage(HttpStatusCode.OK));

        // Rotation: the next batch is signed with the new secret only.
        var rotated = SecretReferences.For(s.T.Id, "siem-new-" + Guid.NewGuid().ToString("N")[..6]);
        world.Factory.Secrets.Set(rotated, "fictional-rotated-secret");
        var updated = await Ok(await s.T["admin"].Client.PutAsJsonAsync($"{Siem}/{id}", new
        {
            name = "Sentinel", transport = "https", url = $"https://{s.Host}/ingest", secretRef = rotated, version = created.GetProperty("version").GetInt32(),
        }));
        Assert.Equal(rotated, updated.GetProperty("secretRef").GetString());
        Assert.True(await StreamAsync(s.T) > 0);
        var last = world.Factory.Rest.SentTo(s.Host).Last();
        var timestamp = long.Parse(last.Headers[WebhookSignature.TimestampHeader], System.Globalization.CultureInfo.InvariantCulture);
        Assert.True(WebhookSignature.Matches(last.Headers[WebhookSignature.SignatureHeader], ["fictional-rotated-secret"], timestamp, last.Body!));
        Assert.False(WebhookSignature.Matches(last.Headers[WebhookSignature.SignatureHeader], [s.Secret], timestamp, last.Body!));

        // Every change is audited with before and after values and no secret.
        var changes = await world.Database.CountInTenantAsync(s.T.Id,
            "select count(*) from audit.audit_event where action = @a and target_id = @id and changes::text not like @secret",
            ("a", AuditActions.SecuritySiemConfigured), ("id", id.ToString()), ("secret", "%fictional%"));
        Assert.Equal(2, changes);
    }

    [Fact]
    public async Task History_days_start_the_stream_back_in_time_and_destinations_are_limited_to_two()
    {
        var s = await FreshAsync();
        await Ok(await s.T["admin"].Client.PutAsJsonAsync("/api/v1/security/policy", new { idleMinutes = 25 }));
        await StreamAsync(s.T);
        var created = await CreateHttpsAsync(s, historyDays: 7);
        Assert.True(created.GetProperty("position").GetInt64() < created.GetProperty("streamEnd").GetInt64());
        Assert.True(await StreamAsync(s.T) > 0);
        Assert.Contains(Events(world.Factory.Rest.SentTo(s.Host)), e => e.GetProperty("action").GetString() == AuditActions.SecurityPolicyChanged);

        await Ok(await s.T["admin"].Client.PostAsJsonAsync(Siem, new { name = "Second", transport = "syslog", host = s.Host }), HttpStatusCode.Created);
        Assert.Equal(HttpStatusCode.Conflict, (await s.T["admin"].Client.PostAsJsonAsync(Siem, new { name = "Third", transport = "syslog", host = s.Host })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await s.T["admin"].Client.PostAsJsonAsync(Siem, new { name = "Old", transport = "https", url = $"https://{s.Host}/x", secretRef = s.SecretRef, historyDays = 8 })).StatusCode);
    }

    [Fact]
    public async Task Only_security_managers_on_a_plan_with_siem_change_destinations_and_tenants_never_see_each_others()
    {
        var s = await FreshAsync();
        var id = (await CreateHttpsAsync(s)).GetProperty("id").GetGuid();

        // Security officers see the destinations but cannot change them.
        Assert.False((await Ok(await s.T["officer"].Client.GetAsync(Siem))).GetProperty("canManage").GetBoolean());
        Assert.True((await Ok(await s.T["admin"].Client.GetAsync(Siem))).GetProperty("canManage").GetBoolean());
        Assert.Equal(HttpStatusCode.Forbidden, (await s.T["officer"].Client.PostAsync($"{Siem}/{id}:test", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await s.T["officer"].Client.DeleteAsync($"{Siem}/{id}")).StatusCode);

        // The destination is hidden from the Connections page.
        var connections = await Ok(await s.T["admin"].Client.GetAsync("/api/v1/integrations/pack-connections"));
        Assert.DoesNotContain("siem", connections.GetRawText(), StringComparison.Ordinal);

        // Another tenant sees none of it, and its stream never carries this tenant's events.
        var other = await FreshAsync();
        Assert.Empty((await Ok(await other.T["admin"].Client.GetAsync(Siem))).GetProperty("destinations").EnumerateArray());
        Assert.Equal(HttpStatusCode.NotFound, (await other.T["admin"].Client.PostAsync($"{Siem}/{id}:test", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.T["admin"].Client.DeleteAsync($"{Siem}/{id}")).StatusCode);
        await CreateHttpsAsync(other);
        await StreamAsync(other.T);
        Assert.All(Events(world.Factory.Rest.SentTo(other.Host)), e => Assert.Equal(other.T.Id, e.GetProperty("tenant").GetGuid()));

        // Workplace has no SIEM streaming.
        var basic = await FreshAsync("workplace");
        Assert.Equal(HttpStatusCode.Forbidden, (await basic.T["admin"].Client.GetAsync(Siem)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await basic.T["admin"].Client.PostAsJsonAsync(Siem, new
        {
            name = "Sentinel", transport = "https", url = $"https://{basic.Host}/ingest", secretRef = basic.SecretRef,
        })).StatusCode);

        // Disabling stops the stream; removing deletes the hidden connection too.
        await Ok(await s.T["admin"].Client.PostAsync($"{Siem}/{id}:disable", null));
        await Ok(await s.T["admin"].Client.PutAsJsonAsync("/api/v1/security/policy", new { idleMinutes = 22 }));
        var count = world.Factory.Rest.SentTo(s.Host).Count;
        await StreamAsync(s.T);
        Assert.Equal(count, world.Factory.Rest.SentTo(s.Host).Count);
        Assert.Equal(HttpStatusCode.NoContent, (await s.T["admin"].Client.DeleteAsync($"{Siem}/{id}")).StatusCode);
        Assert.Equal(0, await world.Database.CountInTenantAsync(s.T.Id, "select count(*) from integrations.connection where kind = 'siem'"));
    }
}
