using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using TeamBeats.Audit.Contracts;
using TeamBeats.Authorization.Contracts;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Primitives;
using TeamBeats.Testing;
using Xunit;

namespace TeamBeats.Integration.Tests;

/// <summary>
/// Spec 026 FR-016 four-eyes approval of critical tenant settings (defaults in the R3 plan): with the switch off changes
/// apply at once; with it on, the security policy, the sign-in configuration and the audit retention wait for another
/// holder of the permission, who approves (applying it) or rejects; the requester can only withdraw; one pending change
/// per kind; the switch needs two administrators and turning it off is held; everything is audited and told; another
/// tenant and people without the permission see nothing.
/// </summary>
[Collection(WorldCollection.Name)]
public sealed class SecurityFourEyesTests(TenantWorld world)
{
    private const string FourEyes = "/api/v1/security/four-eyes";
    private const string Changes = "/api/v1/security/changes";
    private const string Policy = "/api/v1/security/policy";

    private async Task<TestTenant> FreshAsync()
    {
        var t = await world.CreateTenantAsync("fe-" + Guid.NewGuid().ToString("N")[..8], "workplace_plus");
        await world.SeedUserAsync(t, "second", MembershipTypes.Employee);
        await world.SeedUserAsync(t, "emp", MembershipTypes.Employee);
        await world.AssignAsync(t, "second", BuiltInRoles.TenantAdministrator, "tenant", null);
        return t;
    }

    private static async Task<JsonElement> SendAsync(Task<HttpResponseMessage> call, HttpStatusCode expected)
    {
        var response = await call;
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == expected, $"{(int)response.StatusCode}: {text}");
        return text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone();
    }

    private static async Task<string> ProblemAsync(Task<HttpResponseMessage> call, HttpStatusCode expected)
    {
        var body = await SendAsync(call, expected);
        if (body.TryGetProperty("errors", out var errors))
        {
            return errors.EnumerateObject().First().Value[0].GetProperty("key").GetString()!;
        }
        return body.TryGetProperty("detailKey", out var detail) && detail.ValueKind == JsonValueKind.String ? detail.GetString()! : body.GetProperty("code").GetString()!;
    }

    private Task<long> AuditedAsync(TestTenant t, string action) =>
        world.Database.CountInTenantAsync(t.Id, "select count(*) from audit.audit_event where action = @a", ("a", action));

    private Task<long> NotifiedAsync(TestTenant t, string user, string template) =>
        world.Database.CountInTenantAsync(t.Id, "select count(*) from notifications.notification where user_id = @u and template_key = @t",
            ("u", t[user].Id), ("t", template));

    private static async Task<int> IdleAsync(TestTenant t) =>
        (await SendAsync(t["admin"].Client.GetAsync(Policy), HttpStatusCode.OK)).GetProperty("idleMinutes").GetInt32();

    [Fact]
    public async Task A_held_policy_change_applies_only_when_another_administrator_approves_it()
    {
        var t = await FreshAsync();
        // Off: a change applies at once.
        await SendAsync(t["admin"].Client.PutAsJsonAsync(Policy, new { idleMinutes = 40 }), HttpStatusCode.OK);
        Assert.Equal(40, await IdleAsync(t));

        await SendAsync(t["admin"].Client.PutAsJsonAsync(FourEyes, new { enabled = true }), HttpStatusCode.OK);
        Assert.Equal(1L, await AuditedAsync(t, AuditActions.SecurityFourEyesChanged));

        // On: the change is held, nothing changes, and the second administrator is told.
        var held = await SendAsync(t["admin"].Client.PutAsJsonAsync(Policy, new { idleMinutes = 50 }), HttpStatusCode.Accepted);
        var change = held.GetProperty("pendingChange");
        var id = change.GetProperty("id").GetGuid();
        Assert.Equal("security.policy", change.GetProperty("kind").GetString());
        Assert.Equal(["idleMinutes"], change.GetProperty("fields").EnumerateArray().Select(f => f.GetString()));
        Assert.Equal(40, await IdleAsync(t));
        Assert.Equal(1L, await NotifiedAsync(t, "second", "security.change_pending"));
        Assert.Equal(0L, await NotifiedAsync(t, "admin", "security.change_pending"));

        // One pending change per kind; the requester cannot approve their own.
        Assert.Equal("conflict.four_eyes.pending", await ProblemAsync(t["second"].Client.PutAsJsonAsync(Policy, new { idleMinutes = 60 }), HttpStatusCode.Conflict));
        Assert.Equal("conflict.four_eyes.own_change", await ProblemAsync(t["admin"].Client.PostAsync($"{Changes}/{id}:approve", null), HttpStatusCode.Conflict));

        // People without the permission and other tenants see nothing.
        await SendAsync(t["emp"].Client.GetAsync(Changes), HttpStatusCode.Forbidden);
        await SendAsync(t["emp"].Client.PostAsync($"{Changes}/{id}:approve", null), HttpStatusCode.NotFound);
        var other = await FreshAsync();
        await SendAsync(other["second"].Client.PostAsync($"{Changes}/{id}:approve", null), HttpStatusCode.NotFound);

        var listed = await SendAsync(t["second"].Client.GetAsync(Changes), HttpStatusCode.OK);
        var row = Assert.Single(listed.GetProperty("items").EnumerateArray());
        Assert.True(row.GetProperty("canDecide").GetBoolean());

        var approved = await SendAsync(t["second"].Client.PostAsync($"{Changes}/{id}:approve", null), HttpStatusCode.OK);
        Assert.Equal("approved", approved.GetProperty("status").GetString());
        Assert.Equal(50, await IdleAsync(t));
        Assert.Equal(1L, await AuditedAsync(t, AuditActions.SecurityChangeRequested));
        Assert.Equal(1L, await AuditedAsync(t, AuditActions.SecurityChangeApproved));
        Assert.Equal(2L, await AuditedAsync(t, AuditActions.SecurityPolicyChanged));
        Assert.Equal(1L, await NotifiedAsync(t, "admin", "security.change_decided"));
        Assert.Equal("conflict.four_eyes.closed", await ProblemAsync(t["second"].Client.PostAsync($"{Changes}/{id}:approve", null), HttpStatusCode.Conflict));
        Assert.Equal(0L, await AuditedAsync(other, AuditActions.SecurityChangeApproved));
    }

    [Fact]
    public async Task Rejected_withdrawn_and_expired_changes_leave_the_setting_and_retention_and_sign_in_are_held_too()
    {
        var t = await FreshAsync();
        await SendAsync(t["admin"].Client.PutAsJsonAsync(FourEyes, new { enabled = true }), HttpStatusCode.OK);

        // Audit retention: held, then rejected with a reason.
        var retention = await SendAsync(t["admin"].Client.PutAsJsonAsync("/api/v1/audit/retention", new { days = 400 }), HttpStatusCode.Accepted);
        var retentionId = retention.GetProperty("pendingChange").GetProperty("id").GetGuid();
        Assert.Equal(365, retention.GetProperty("days").GetInt32());
        await SendAsync(t["second"].Client.PostAsJsonAsync($"{Changes}/{retentionId}:reject", new { reason = "Keep the minimum for now." }), HttpStatusCode.OK);
        Assert.Equal(365, (await SendAsync(t["admin"].Client.GetAsync("/api/v1/audit/retention"), HttpStatusCode.OK)).GetProperty("days").GetInt32());
        Assert.Equal(1L, await AuditedAsync(t, AuditActions.SecurityChangeRejected));

        // A held policy change withdrawn by its requester; only the requester may withdraw.
        var held = await SendAsync(t["admin"].Client.PutAsJsonAsync(Policy, new { idleMinutes = 45 }), HttpStatusCode.Accepted);
        var heldId = held.GetProperty("pendingChange").GetProperty("id").GetGuid();
        await SendAsync(t["second"].Client.PostAsync($"{Changes}/{heldId}:withdraw", null), HttpStatusCode.NotFound);
        await SendAsync(t["admin"].Client.PostAsync($"{Changes}/{heldId}:withdraw", null), HttpStatusCode.OK);
        Assert.Equal(30, await IdleAsync(t));
        Assert.Equal(1L, await AuditedAsync(t, AuditActions.SecurityChangeWithdrawn));

        // A held change past 72 hours expires and can no longer be approved.
        var late = await SendAsync(t["admin"].Client.PutAsJsonAsync(Policy, new { idleMinutes = 55 }), HttpStatusCode.Accepted);
        var lateId = late.GetProperty("pendingChange").GetProperty("id").GetGuid();
        await world.Database.TenantScalarAsync<int>(t.Id,
            "update security.pending_change set requested_at = now() - interval '4 days', expires_at = now() - interval '1 day' where id = @id returning 1", ("id", lateId));
        Assert.Equal("conflict.four_eyes.closed", await ProblemAsync(t["second"].Client.PostAsync($"{Changes}/{lateId}:approve", null), HttpStatusCode.Conflict));
        await SendAsync(t["second"].Client.GetAsync(Changes), HttpStatusCode.OK);
        Assert.Equal(1L, await AuditedAsync(t, AuditActions.SecurityChangeExpired));
        Assert.Equal(30, await IdleAsync(t));

        // The sign-in configuration is held as well.
        var sso = await SendAsync(t["admin"].Client.PutAsJsonAsync("/api/v1/tenant/identity-provider", new
        {
            kind = "entra", issuer = FakeIdentityProvider.IssuerFor(Guid.NewGuid()), clientId = "7d0b4bb8-6d1e-4c0e-9a0f-2a8f5f0b1c01",
            secretRef = SecretReferences.For(t.Id, "entra-client"), displayName = "Contoso", jitEnabled = false, jitDomains = Array.Empty<string>(),
            groupRoleMap = Array.Empty<object>(),
        }), HttpStatusCode.Accepted);
        Assert.Equal("identity.provider", sso.GetProperty("pendingChange").GetProperty("kind").GetString());
        await SendAsync(t["admin"].Client.GetAsync("/api/v1/tenant/identity-provider"), HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task The_switch_needs_two_administrators_and_turning_it_off_waits_for_the_other()
    {
        var alone = await world.CreateTenantAsync("fe-" + Guid.NewGuid().ToString("N")[..8], "workplace_plus");
        Assert.Equal("validation.four_eyes.one_holder", await ProblemAsync(alone["admin"].Client.PutAsJsonAsync(FourEyes, new { enabled = true }), HttpStatusCode.BadRequest));

        var t = await FreshAsync();
        await SendAsync(t["admin"].Client.PutAsJsonAsync(FourEyes, new { enabled = true }), HttpStatusCode.OK);
        var off = await SendAsync(t["admin"].Client.PutAsJsonAsync(FourEyes, new { enabled = false }), HttpStatusCode.Accepted);
        Assert.True(off.GetProperty("enabled").GetBoolean());
        var id = off.GetProperty("pending").GetProperty("id").GetGuid();
        Assert.True((await SendAsync(t["admin"].Client.GetAsync(FourEyes), HttpStatusCode.OK)).GetProperty("enabled").GetBoolean());
        await SendAsync(t["second"].Client.PostAsync($"{Changes}/{id}:approve", null), HttpStatusCode.OK);
        Assert.False((await SendAsync(t["admin"].Client.GetAsync(FourEyes), HttpStatusCode.OK)).GetProperty("enabled").GetBoolean());

        // Off again: changes apply at once.
        await SendAsync(t["admin"].Client.PutAsJsonAsync(Policy, new { idleMinutes = 35 }), HttpStatusCode.OK);
        Assert.Equal(35, await IdleAsync(t));
    }
}
