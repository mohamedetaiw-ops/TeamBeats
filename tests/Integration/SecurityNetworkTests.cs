using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using TeamBeats.Audit.Contracts;
using TeamBeats.Modules.Authorization;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Testing;
using Xunit;

namespace TeamBeats.Integration.Tests;

/// <summary>
/// Spec 026 T004, T005 and T007: tenant allow and block lists, the admin area list, the caller address from trusted
/// proxies only, lock-out confirmation, the lock-out preview, plan limits, permissions, audit and tenant isolation.
/// Each test uses its own tenant so its rules never reach the shared tenants.
/// </summary>
[Collection(WorldCollection.Name)]
public sealed class SecurityNetworkTests(TenantWorld world)
{
    private const string Office = "203.0.113.5";
    private const string OfficeRange = "203.0.113.0/24";
    private const string Outside = "198.51.100.7";

    private async Task<TestTenant> FreshAsync(string package = "workplace_plus") =>
        await world.CreateTenantAsync("sec-" + Guid.NewGuid().ToString("N")[..8], package);

    /// <summary>A client for the user that arrives through the proxy from the given address.</summary>
    private HttpClient From(TestTenant tenant, string user, string forwardedFor)
    {
        var client = world.Factory.TenantClient(tenant.Key, tenant[user].Token);
        client.DefaultRequestHeaders.TryAddWithoutValidation("X-Forwarded-For", forwardedFor);
        return client;
    }

    private static async Task<JsonElement> OkAsync(HttpResponseMessage response)
    {
        Assert.True(response.IsSuccessStatusCode, $"{(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static async Task AssertRefusedAsync(HttpResponseMessage response, string area)
    {
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("security.network_not_allowed", body.GetProperty("code").GetString());
        Assert.Equal(area, body.GetProperty("area").GetString());
    }

    private static async Task<string[]> ErrorKeysAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var errors = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("errors");
        return errors.EnumerateObject().SelectMany(p => p.Value.EnumerateArray().Select(e => e.GetProperty("key").GetString()!)).ToArray();
    }

    private static Task<HttpResponseMessage> PutAsync(HttpClient client, object body, string? etag = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Put, "/api/v1/security/policy") { Content = JsonContent.Create(body) };
        if (etag is not null)
        {
            request.Headers.TryAddWithoutValidation("If-Match", etag);
        }
        return client.SendAsync(request);
    }

    [Fact]
    public async Task An_allowlist_refuses_other_addresses_before_any_module_and_audits_once_a_minute()
    {
        var t = await FreshAsync();
        await world.SeedUserAsync(t, "employee", MembershipTypes.Employee);
        var admin = From(t, "admin", Office);

        var view = await OkAsync(await admin.GetAsync("/api/v1/security/policy"));
        Assert.Equal(Office, view.GetProperty("yourAddress").GetString());
        Assert.Empty(view.GetProperty("ipAllow").EnumerateArray());
        var etag = view.GetProperty("eTag").GetString();

        var saved = await OkAsync(await PutAsync(admin, new { ipAllow = new[] { OfficeRange, "203.0.113.0/24" } }, etag));
        Assert.Equal([OfficeRange], saved.GetProperty("ipAllow").EnumerateArray().Select(e => e.GetString()));
        await world.Database.AssertAuditedAsync(t.Id, AuditActions.SecurityPolicyChanged);
        Assert.Equal(1, await world.Database.OutboxCountAsync("SecurityPolicyChanged", t.Id));

        Assert.Equal(HttpStatusCode.OK, (await From(t, "employee", "203.0.113.200").GetAsync("/api/v1/me")).StatusCode);
        var outside = From(t, "employee", Outside);
        await AssertRefusedAsync(await outside.GetAsync("/api/v1/me"), "tenant");
        await AssertRefusedAsync(await outside.GetAsync("/api/v1/people"), "tenant");
        // Anonymous requests to the tenant host are refused too, so no sign-in can start from outside.
        var anonymous = world.Factory.TenantClient(t.Key);
        anonymous.DefaultRequestHeaders.TryAddWithoutValidation("X-Forwarded-For", Outside);
        await AssertRefusedAsync(await anonymous.GetAsync("/api/v1/auth/signin/options"), "tenant");

        Assert.Equal(1, await world.Database.AuditCountAsync(t.Id, AuditActions.SecurityRequestBlockedNetwork, Outside));
        Assert.Equal(1, await world.Database.CountInTenantAsync(t.Id,
            "select count(*) from audit.audit_event where action = 'security.request_blocked_network' and outcome = 'denied' and changes->>'area' = 'tenant'"));
    }

    [Fact]
    public async Task The_blocklist_wins_over_the_allowlist()
    {
        var t = await FreshAsync("workplace");
        await world.SeedUserAsync(t, "employee", MembershipTypes.Employee);
        await OkAsync(await PutAsync(From(t, "admin", Office), new { ipAllow = new[] { OfficeRange }, ipBlock = new[] { "203.0.113.66" } }));

        Assert.Equal(HttpStatusCode.OK, (await From(t, "employee", "203.0.113.65").GetAsync("/api/v1/me")).StatusCode);
        await AssertRefusedAsync(await From(t, "employee", "203.0.113.66").GetAsync("/api/v1/me"), "tenant");
    }

    [Fact]
    public async Task Only_the_proxy_appended_address_counts_so_a_spoofed_leftmost_entry_is_ignored()
    {
        var t = await FreshAsync("workplace");
        await world.SeedUserAsync(t, "employee", MembershipTypes.Employee);
        await OkAsync(await PutAsync(From(t, "admin", Office), new { ipAllow = new[] { OfficeRange } }));

        // The client claims an office address; the proxy appends the real one.
        await AssertRefusedAsync(await From(t, "employee", $"{Office}, {Outside}").GetAsync("/api/v1/me"), "tenant");
        Assert.Equal(HttpStatusCode.OK, (await From(t, "employee", $"{Outside}, {Office}").GetAsync("/api/v1/me")).StatusCode);
    }

    [Fact]
    public async Task The_admin_area_list_guards_admin_endpoints_only_and_a_self_lockout_needs_the_address_typed_back()
    {
        var t = await FreshAsync();
        var admin = From(t, "admin", Office);

        var refused = await PutAsync(admin, new { adminIpAllow = new[] { "10.0.0.0/8" } });
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        var problem = JsonDocument.Parse(await refused.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(Office, problem.GetProperty("yourAddress").GetString());
        Assert.Equal("validation.security.lockout_confirm", problem.GetProperty("errors").GetProperty("confirmation")[0].GetProperty("key").GetString());
        Assert.Equal(0, await world.Database.AuditCountAsync(t.Id, AuditActions.SecurityPolicyChanged));

        Assert.Equal(["validation.security.lockout_confirm"],
            await ErrorKeysAsync(await PutAsync(admin, new { adminIpAllow = new[] { "10.0.0.0/8" }, confirmation = "10.0.0.1" })));
        await OkAsync(await PutAsync(admin, new { adminIpAllow = new[] { "10.0.0.0/8" }, confirmation = Office }));

        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync("/api/v1/me")).StatusCode);
        await AssertRefusedAsync(await admin.GetAsync("/api/v1/security/policy"), "admin");
        await AssertRefusedAsync(await admin.GetAsync("/api/v1/admin/navigation"), "admin");
        await AssertRefusedAsync(await admin.GetAsync("/api/v1/authz/roles"), "admin");
        await AssertRefusedAsync(await admin.GetAsync("/api/v1/audit/events"), "admin");

        var vpn = From(t, "admin", "10.1.2.3");
        Assert.Equal(HttpStatusCode.OK, (await vpn.GetAsync("/api/v1/admin/navigation")).StatusCode);
        // Clearing the list from inside it needs no confirmation.
        var cleared = await OkAsync(await PutAsync(vpn, new { adminIpAllow = Array.Empty<string>() }));
        Assert.Empty(cleared.GetProperty("adminIpAllow").EnumerateArray());
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync("/api/v1/security/policy")).StatusCode);
        Assert.Equal(1, await world.Database.CountInTenantAsync(t.Id,
            "select count(*) from audit.audit_event where action = 'security.request_blocked_network' and changes->>'area' = 'admin'"));
    }

    [Fact]
    public async Task The_Workplace_plan_keeps_tenant_rules_but_refuses_the_admin_area_list()
    {
        var t = await FreshAsync("workplace");
        var admin = From(t, "admin", Office);
        Assert.False((await OkAsync(await admin.GetAsync("/api/v1/security/policy"))).GetProperty("scopedRulesAvailable").GetBoolean());

        var refused = await PutAsync(admin, new { adminIpAllow = new[] { OfficeRange } });
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        var body = await refused.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("entitlement.not_in_package", body.GetProperty("code").GetString());
        Assert.Equal("security.ip_rules_scoped", body.GetProperty("capability").GetString());
        Assert.Equal(HttpStatusCode.Forbidden, (await PutAsync(admin, new { networkZones = new Dictionary<string, string[]> { ["office"] = [OfficeRange] } })).StatusCode);

        await OkAsync(await PutAsync(admin, new { ipBlock = new[] { Outside } }));
    }

    [Fact]
    public async Task Security_officers_read_the_policy_and_only_tenant_administrators_change_it()
    {
        var t = await FreshAsync();
        await world.SeedUserAsync(t, "officer", MembershipTypes.Employee);
        await world.SeedUserAsync(t, "employee", MembershipTypes.Employee);
        await world.AssignAsync(t, "officer", "security_officer", "tenant", null);

        var officer = From(t, "officer", Office);
        Assert.Equal(HttpStatusCode.OK, (await officer.GetAsync("/api/v1/security/policy")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await PutAsync(officer, new { ipBlock = new[] { Outside } })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await officer.GetAsync($"/api/v1/security/policy/preview?ipAllow={OfficeRange}")).StatusCode);

        var employee = From(t, "employee", Office);
        Assert.Equal(HttpStatusCode.Forbidden, (await employee.GetAsync("/api/v1/security/policy")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await PutAsync(employee, new { ipBlock = new[] { Outside } })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await world.Factory.TenantClient(t.Key).GetAsync("/api/v1/security/policy")).StatusCode);
    }

    [Fact]
    public async Task Rules_of_one_tenant_never_reach_another_and_a_session_cannot_cross_hosts()
    {
        var x = await FreshAsync();
        var y = await FreshAsync();
        await world.SeedUserAsync(y, "employee", MembershipTypes.Employee);
        await OkAsync(await PutAsync(From(x, "admin", Office), new { ipAllow = new[] { OfficeRange } }));

        Assert.Equal(HttpStatusCode.OK, (await From(y, "employee", Outside).GetAsync("/api/v1/me")).StatusCode);
        var yPolicy = await OkAsync(await From(y, "admin", Outside).GetAsync("/api/v1/security/policy"));
        Assert.Empty(yPolicy.GetProperty("ipAllow").EnumerateArray());

        // X's administrator session on Y's host finds no session, so X's policy cannot be read or changed there.
        var crossed = world.Factory.TenantClient(y.Key, x["admin"].Token);
        Assert.Equal(HttpStatusCode.Unauthorized, (await crossed.GetAsync("/api/v1/security/policy")).StatusCode);
        Assert.Equal(1, await world.Database.CountInTenantAsync(x.Id, "select count(*) from security.policy"));
        Assert.Equal(0, await world.Database.CountInTenantAsync(y.Id, "select count(*) from security.policy where cardinality(ip_allow) > 0"));
    }

    [Fact]
    public async Task Invalid_values_and_stale_versions_are_refused()
    {
        var t = await FreshAsync();
        var admin = From(t, "admin", Office);

        var keys = await ErrorKeysAsync(await PutAsync(admin, new
        {
            ipAllow = new[] { "not an address", "203.0.113.5/24" },
            networkZones = new Dictionary<string, string[]> { ["Head Office"] = [OfficeRange] },
            idleMinutes = 2,
            absoluteHours = 100,
            maxSessions = 0,
            rememberDeviceDays = 91,
            stepUpMinutes = 0,
        }));
        Assert.Contains("validation.security.range", keys);
        Assert.Contains("validation.security.zone_name", keys);
        Assert.Contains("validation.security.out_of_range", keys);
        Assert.Equal(["validation.security.too_many"],
            await ErrorKeysAsync(await PutAsync(admin, new { ipBlock = Enumerable.Range(0, 201).Select(i => $"10.0.{i / 256}.{i % 256}").ToArray() })));
        Assert.Equal(["validation.security.mfa_not_seen"], await ErrorKeysAsync(await PutAsync(admin, new { requireMfa = true })));

        var first = await OkAsync(await PutAsync(admin, new { idleMinutes = 45, maxSessions = 3 }));
        Assert.Equal(45, first.GetProperty("idleMinutes").GetInt32());
        var stale = await PutAsync(admin, new { idleMinutes = 60 }, "\"v1\"");
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        Assert.Equal("conflict.security.policy_changed", (await stale.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("detailKey").GetString());
        var unlimited = await OkAsync(await PutAsync(admin, new { noSessionLimit = true }, first.GetProperty("eTag").GetString()));
        Assert.Equal(JsonValueKind.Null, unlimited.GetProperty("maxSessions").ValueKind);

        Assert.Equal(1, await world.Database.CountInTenantAsync(t.Id,
            """
            select count(*) from audit.audit_event where action = 'security.policy_changed'
              and changes->'settings' ? 'idleMinutes' and (changes->'before'->>'idleMinutes')::int = 30 and (changes->'after'->>'idleMinutes')::int = 45
            """));
    }

    [Fact]
    public async Task The_preview_counts_people_whose_recent_sign_ins_the_proposed_rules_would_refuse()
    {
        var t = await FreshAsync();
        await world.SeedUserAsync(t, "inside", MembershipTypes.Employee);
        await world.SeedUserAsync(t, "travelling", MembershipTypes.Employee);
        await world.SeedUserAsync(t, "away", MembershipTypes.Employee);
        async Task SignedInFromAsync(string user, string address, int daysAgo) =>
            await world.Database.OwnerExecInTenantAsync(t.Id,
                "update identity.user_session set source_ip = @a::inet, created_at = now() - make_interval(days => @d) where user_id = @u",
                ("a", address), ("d", daysAgo), ("u", t[user].Id));
        await SignedInFromAsync("admin", Office, 1);
        await SignedInFromAsync("inside", "203.0.113.40", 2);
        await SignedInFromAsync("travelling", Outside, 3);
        await SignedInFromAsync("away", "192.0.2.10", 45); // Older than 30 days: not counted.

        var admin = From(t, "admin", Office);
        var preview = await OkAsync(await admin.GetAsync($"/api/v1/security/policy/preview?ipAllow={Uri.EscapeDataString(OfficeRange)}"));
        Assert.Equal(1, preview.GetProperty("peopleOutside").GetInt32());
        Assert.Equal(Office, preview.GetProperty("yourAddress").GetString());
        Assert.False(preview.GetProperty("youWouldBeRefused").GetBoolean());

        var blocked = await OkAsync(await admin.GetAsync($"/api/v1/security/policy/preview?ipBlock={Office}"));
        Assert.Equal(1, blocked.GetProperty("peopleOutside").GetInt32());
        Assert.True(blocked.GetProperty("youWouldBeRefused").GetBoolean());
        Assert.Equal(["validation.security.range"], await ErrorKeysAsync(await admin.GetAsync("/api/v1/security/policy/preview?ipAllow=nope")));
        Assert.Equal(0, await world.Database.AuditCountAsync(t.Id, AuditActions.SecurityPolicyChanged));
    }

    [Fact]
    public async Task Security_backfill_gives_an_older_tenants_administrators_and_officers_the_security_permissions()
    {
        var old = await FreshAsync("workplace");
        await world.SeedUserAsync(old, "officer", MembershipTypes.Employee);
        await world.AssignAsync(old, "officer", "security_officer", "tenant", null);
        await world.Database.OwnerExecAsync(
            """
            select set_config('app.tenant_id', @t, false);
            delete from authz.role_permission where permission_key like 'security.%';
            update authz.grants_version set version = version + 1;
            """, ("t", old.Id.ToString()));
        Assert.Equal(HttpStatusCode.Forbidden, (await From(old, "admin", Office).GetAsync("/api/v1/security/policy")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await From(old, "officer", Office).GetAsync("/api/v1/security/policy")).StatusCode);

        var script = AuthorizationModule.Migrations.Scripts().Single(s => s.Name.StartsWith("0011_", StringComparison.Ordinal)).Sql;
        await world.Database.OwnerExecAsync(script);
        await world.Database.OwnerExecAsync(script);

        Assert.Equal(HttpStatusCode.OK, (await From(old, "admin", Office).GetAsync("/api/v1/security/policy")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await PutAsync(From(old, "admin", Office), new { ipBlock = new[] { Outside } })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await From(old, "officer", Office).GetAsync("/api/v1/security/policy")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await PutAsync(From(old, "officer", Office), new { ipBlock = new[] { "192.0.2.1" } })).StatusCode);
    }
    [Fact]
    public async Task A_network_zone_condition_limits_one_grant_to_named_networks_and_the_explainer_names_it()
    {
        var t = await FreshAsync();
        await world.SeedUserAsync(t, "approver", MembershipTypes.Employee);
        var admin = From(t, "admin", Office);
        await OkAsync(await PutAsync(admin, new
        {
            networkZones = new Dictionary<string, string[]> { ["office"] = [OfficeRange], ["vpn"] = ["10.0.0.0/8"] },
        }));
        var assigned = await admin.PostAsJsonAsync("/api/v1/authz/assignments", new
        {
            subjectType = "user", subjectId = t["approver"].Id, roleKey = "security_officer", scopeType = "tenant", scopeId = (Guid?)null,
            condition = new { attribute = "network_zone", @operator = "in", value = new[] { "office", "vpn" } },
        });
        Assert.True(assigned.IsSuccessStatusCode, await assigned.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.OK, (await From(t, "approver", "203.0.113.77").GetAsync("/api/v1/security/policy")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await From(t, "approver", "10.9.8.7").GetAsync("/api/v1/security/policy")).StatusCode);
        var outside = From(t, "approver", Outside);
        var denied = await outside.GetAsync("/api/v1/security/policy");
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        Assert.Equal("authz.denied", (await denied.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
        // Access from other grants continues outside the zones.
        Assert.Equal(HttpStatusCode.OK, (await outside.GetAsync("/api/v1/people")).StatusCode);

        var explained = await OkAsync(await admin.GetAsync($"/api/v1/authz/explain?userId={t["approver"].Id}&permission=security.view"));
        Assert.False(explained.GetProperty("allowed").GetBoolean());
        var trace = explained.GetProperty("notApplied")[0].GetProperty("condition");
        Assert.Equal("network_zone", trace.GetProperty("clauses")[0].GetProperty("attribute").GetString());
        Assert.Equal("network_zone", trace.GetProperty("missingAttributes")[0].GetString());
    }
}
