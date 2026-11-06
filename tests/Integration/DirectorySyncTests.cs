using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TeamBeats.Audit.Contracts;
using TeamBeats.Messaging;
using TeamBeats.Modules.Identity.Directory;
using TeamBeats.Persistence;
using TeamBeats.Primitives;
using TeamBeats.Testing;
using Xunit;

namespace TeamBeats.Integration.Tests;

/// <summary>Spec 003 User Story 6 (R1 for Entra ID): directory sync through the Microsoft 365 connection (tasks T021 to T025).</summary>
[Collection(WorldCollection.Name)]
public sealed class DirectorySyncTests(TenantWorld world)
{
    private const string ClientId = "7d0b4bb8-6d1e-4c0e-9a0f-2a8f5f0b1c01";
    private const string ClientSecret = "fictional-test-client-secret";
    private static readonly string[] DirectoryRoles = ["User.Read.All", "Group.Read.All", "AdministrativeUnit.Read.All"];

    private FakeMicrosoftGraph Graph => world.Factory.Graph;

    private sealed record Synced(TestTenant Tenant, Guid Directory, Guid Connection, FakeMicrosoftGraph.DirectoryData Data)
    {
        public string Key => Tenant.Key;
        public Guid Id => Tenant.Id;
        public string Email(string name) => $"{name}@{Tenant.Key}.example";
    }

    private static string Oid() => Guid.NewGuid().ToString("D");

    /// <summary>A fresh tenant with a consented Microsoft 365 connection that has directory sync on, and sync settings saved.</summary>
    private async Task<Synced> TenantAsync(object? settings = null, bool sso = false, object[]? groupRoleMap = null)
    {
        var t = await world.CreateTenantAsync("dir-" + Guid.NewGuid().ToString("N")[..8], "workplace_plus");
        var directory = Guid.NewGuid();
        var admin = t["admin"].Client;
        var created = await admin.PostAsJsonAsync("/api/v1/integrations/connections", new
        {
            kind = "microsoft365", externalTenantId = directory, displayName = "Contoso Microsoft 365", features = new[] { "directory_sync" },
        });
        var connection = (await created.ReadAsync<JsonElement>()).GetProperty("id").GetGuid();
        Graph.Consent(directory, DirectoryRoles);
        await ConsentAsync(t, connection, directory);

        if (sso || groupRoleMap is not null)
        {
            var secretRef = SecretReferences.For(t.Id, "entra-client");
            world.Factory.Secrets.Set(secretRef, ClientSecret);
            (await admin.PutAsJsonAsync("/api/v1/tenant/identity-provider", new
            {
                kind = "entra", issuer = FakeIdentityProvider.IssuerFor(directory), clientId = ClientId, secretRef, displayName = "Contoso",
                jitEnabled = true, jitDomains = new[] { $"{t.Key}.example" }, groupRoleMap = groupRoleMap ?? [],
            })).EnsureSuccessStatusCode();
        }

        var saved = await admin.PutAsJsonAsync("/api/v1/tenant/directory", settings ?? new { userScope = new { mode = "all", groups = Array.Empty<string>() }, deletionThresholdPct = 100 });
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        return new Synced(t, directory, connection, Graph.Data(directory));
    }

    private async Task ConsentAsync(TestTenant t, Guid connection, Guid directory)
    {
        var start = await t["admin"].Client.GetAsync($"/api/v1/integrations/connections/{connection}/features/directory_sync/consent");
        var query = FakeIdentityProvider.ParseQuery(start.Headers.Location!.Query);
        var relay = await world.Factory.PlatformClient().GetAsync(
            $"/api/v1/integrations/consent/callback?admin_consent=True&tenant={directory:D}&state={Uri.EscapeDataString(query["state"])}");
        var client = world.Factory.TenantClient(t.Key, t["admin"].Token);
        var binding = start.Headers.GetValues("Set-Cookie").Select(v => v.Split(';')[0]).First(v => v.StartsWith("tb_consent=", StringComparison.Ordinal));
        client.DefaultRequestHeaders.Add("Cookie", binding);
        var done = await client.GetAsync(relay.Headers.Location!.PathAndQuery);
        Assert.Contains("consent=granted", done.Headers.Location!.ToString(), StringComparison.Ordinal);
    }

    private Task<SyncRun> RunAsync(Synced s) =>
        world.Factory.InScopeAsync(s.Id, async sp =>
        {
            var run = await sp.GetRequiredService<DirectorySyncService>().RunAsync(s.Connection, CancellationToken.None);
            await sp.GetRequiredService<IDbSession>().CommitAsync();
            return run!;
        });

    private Task<Guid?> UserIdAsync(Synced s, string oid) =>
        world.Factory.InScopeAsync(s.Id, sp => sp.GetRequiredService<IDbSession>().ScalarAsync<Guid?>(
            "select user_id from identity.directory_user_link where directory_id = @d", new { d = oid }));

    private Task<string?> MembershipStatusAsync(Synced s, Guid userId) =>
        world.Factory.InScopeAsync(s.Id, sp => sp.GetRequiredService<IDbSession>().ScalarAsync<string?>(
            "select status from tenancy.tenant_membership where user_id = @u", new { u = userId }));

    private Task<long> CountAsync(Synced s, string sql) => world.Database.CountInTenantAsync(s.Id, sql);

    [Fact]
    public async Task First_sync_creates_users_keyed_by_object_id_and_a_second_run_changes_nothing()
    {
        var s = await TenantAsync();
        var (alice, bob, carol, nomail) = (Oid(), Oid(), Oid(), Oid());
        s.Data.AddUser(alice, "Alice Hassan", s.Email("Alice"), jobTitle: "Engineer");
        s.Data.AddUser(bob, "Bob Saleh", s.Email("bob"));
        s.Data.AddUser(carol, "Carol Disabled", s.Email("carol"), enabled: false);
        s.Data.AddUser(nomail, "No Mail", null);

        var run = await RunAsync(s);
        Assert.Equal("succeeded", run.Outcome);
        Assert.Equal(2, run.Created);
        Assert.Equal(1, run.Skipped);

        var aliceId = (await UserIdAsync(s, alice))!.Value;
        Assert.Equal(1, await CountAsync(s,
            $"select count(*) from identity.user_account where id = '{aliceId}' and created_via = 'directory' and email = '{s.Email("alice")}' and owner_fields = array['display_name','email']"));
        Assert.Equal(1, await CountAsync(s,
            $"select count(*) from identity.external_identity where user_id = '{aliceId}' and issuer = '{FakeIdentityProvider.IssuerFor(s.Directory)}' and subject = '{alice}'"));
        Assert.Equal("active", await MembershipStatusAsync(s, aliceId));
        Assert.Null(await UserIdAsync(s, carol));
        await world.Database.AssertAuditedAsync(s.Id, AuditActions.UserSynced, aliceId.ToString());
        Assert.True(await world.Database.OutboxCountAsync("UserProvisioned", s.Id) >= 2);

        // Idempotent and incremental: nothing changed in the directory, so nothing changes here.
        var again = await RunAsync(s);
        Assert.Equal((0, 0, 0, 0), (again.Created, again.Updated, again.Disabled, again.Reenabled));
        Assert.Equal(2, await CountAsync(s, "select count(*) from identity.user_account where created_via = 'directory'"));
    }

    [Fact]
    public async Task Synced_job_title_is_a_condition_attribute_and_product_users_have_none()
    {
        var s = await TenantAsync();
        var (engineer, analyst) = (Oid(), Oid());
        s.Data.AddUser(engineer, "Eman Engineer", s.Email("eman"), jobTitle: "Engineer");
        s.Data.AddUser(analyst, "Omar Analyst", s.Email("omar"), jobTitle: "Analyst");
        Assert.Equal("succeeded", (await RunAsync(s)).Outcome);

        var admin = s.Tenant["admin"].Client;
        var condition = new { attribute = "job_title", @operator = "eq", value = "engineer" };
        var productUser = await world.SeedUserAsync(s.Tenant, "product_only", "employee");
        var ids = new[] { (await UserIdAsync(s, engineer))!.Value, (await UserIdAsync(s, analyst))!.Value, productUser.Id };
        var allowed = new List<bool>();
        foreach (var id in ids)
        {
            (await admin.PostAsJsonAsync("/api/v1/authz/assignments",
                new { subjectType = "user", subjectId = id, roleKey = "approver", scopeType = "tenant", condition })).EnsureSuccessStatusCode();
            var explained = await admin.GetFromJsonAsync<JsonElement>($"/api/v1/authz/explain?userId={id}&permission=workflow.approve");
            allowed.Add(explained.GetProperty("allowed").GetBoolean());
        }

        // The engineer matches; the analyst does not; the product user has no directory record, so the condition fails closed.
        Assert.Equal([true, false, false], allowed);
    }

    [Fact]
    public async Task Rename_and_email_change_update_the_same_user_in_place()
    {
        var s = await TenantAsync();
        var oid = Oid();
        s.Data.AddUser(oid, "Huda Old", s.Email("huda.old"));
        await RunAsync(s);
        var userId = (await UserIdAsync(s, oid))!.Value;

        s.Data.UpdateUser(oid, displayName: "Huda New", mail: s.Email("Huda.New"));
        var run = await RunAsync(s);
        Assert.Equal(1, run.Updated);
        Assert.Equal(userId, await UserIdAsync(s, oid));
        Assert.Equal(1, await CountAsync(s, $"select count(*) from identity.user_account where id = '{userId}' and display_name = 'Huda New' and email = '{s.Email("huda.new")}'"));
        Assert.True(await world.Database.OutboxCountAsync("UserProfileSynced", s.Id) >= 1);
    }

    [Fact]
    public async Task Disable_in_the_directory_revokes_sessions_blocks_product_re_enable_and_re_enable_restores_the_same_user()
    {
        var s = await TenantAsync();
        var oid = Oid();
        s.Data.AddUser(oid, "Sami Leaver", s.Email("sami"));
        await RunAsync(s);
        var userId = (await UserIdAsync(s, oid))!.Value;
        var token = await world.Factory.InScopeAsync(s.Id, async sp =>
        {
            var value = await sp.GetRequiredService<TeamBeats.Modules.Identity.SessionService>().CreateUserSessionAsync(userId, "entra", CancellationToken.None);
            await sp.GetRequiredService<IDbSession>().CommitAsync();
            return value;
        });
        var sami = world.Factory.TenantClient(s.Key, token);
        Assert.Equal(HttpStatusCode.OK, (await sami.GetAsync("/api/v1/me")).StatusCode);

        s.Data.UpdateUser(oid, enabled: false);
        var run = await RunAsync(s);
        Assert.Equal(1, run.Disabled);
        Assert.Equal("disabled", await MembershipStatusAsync(s, userId));
        Assert.Equal(0, await CountAsync(s, $"select count(*) from identity.user_session where user_id = '{userId}' and revoked_at is null"));
        Assert.Equal(HttpStatusCode.Unauthorized, (await sami.GetAsync("/api/v1/me")).StatusCode);
        await world.Database.AssertAuditedAsync(s.Id, AuditActions.DisabledByDirectory, userId.ToString());
        Assert.True(await world.Database.OutboxCountAsync("UserDeprovisioned", s.Id) >= 1);

        // FR-013: a product admin cannot re-enable while the directory still has the account disabled.
        var enable = await s.Tenant["admin"].Client.PostAsync($"/api/v1/admin/users/{userId}:enable", null);
        Assert.Equal(HttpStatusCode.Conflict, enable.StatusCode);
        Assert.Contains("conflict.user.disabled_by_directory", await enable.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        s.Data.UpdateUser(oid, enabled: true);
        var back = await RunAsync(s);
        Assert.Equal(1, back.Reenabled);
        Assert.Equal(userId, await UserIdAsync(s, oid));
        Assert.Equal("active", await MembershipStatusAsync(s, userId));
        await world.Database.AssertAuditedAsync(s.Id, AuditActions.ReenabledByDirectory, userId.ToString());
    }

    [Fact]
    public async Task Deleted_account_is_disabled_and_kept()
    {
        var s = await TenantAsync();
        var oid = Oid();
        s.Data.AddUser(oid, "Gone Soon", s.Email("gone"));
        await RunAsync(s);
        var userId = (await UserIdAsync(s, oid))!.Value;

        s.Data.DeleteUser(oid);
        Assert.Equal(1, (await RunAsync(s)).Disabled);
        Assert.Equal("disabled", await MembershipStatusAsync(s, userId));
        Assert.Equal(1, await CountAsync(s, $"select count(*) from identity.user_account where id = '{userId}'"));
    }

    [Fact]
    public async Task Synced_user_signs_in_through_the_object_id_with_no_duplicate_and_jit_is_off_while_sync_runs()
    {
        var s = await TenantAsync(sso: true);
        var oid = Oid();
        s.Data.AddUser(oid, "Rana Synced", s.Email("rana"));
        await RunAsync(s);
        var userId = (await UserIdAsync(s, oid))!.Value;

        var landed = await SignInAsync(s, new FakeUser(oid, s.Email("rana"), "Rana Synced"));
        Assert.Equal("/", landed.Location);
        Assert.Equal(1, await CountAsync(s, $"select count(*) from identity.user_account where lower(email) = '{s.Email("rana")}'"));
        Assert.Equal(1, await CountAsync(s, $"select count(*) from identity.user_session where user_id = '{userId}'"));

        // An allowed-domain account that is not in the directory scope gets no JIT account.
        var stranger = await SignInAsync(s, new FakeUser(Oid(), s.Email("stranger"), "Stranger"));
        Assert.StartsWith("/signin/problem", stranger.Location, StringComparison.Ordinal);
        Assert.Equal(0, await CountAsync(s, $"select count(*) from identity.user_account where email = '{s.Email("stranger")}'"));
    }

    [Fact]
    public async Task Moving_the_scope_to_selected_groups_disables_users_outside_it_without_reading_everything_again()
    {
        var s = await TenantAsync();
        var (inside, outside, group) = (Oid(), Oid(), Oid());
        s.Data.AddUser(inside, "Inside Scope", s.Email("inside"));
        s.Data.AddUser(outside, "Outside Scope", s.Email("outside"));
        s.Data.AddGroup(group, "Intranet users");
        s.Data.AddMember(group, inside);
        await RunAsync(s);

        (await s.Tenant["admin"].Client.PutAsJsonAsync("/api/v1/tenant/directory", new { userScope = new { mode = "groups", groups = new[] { group } } })).EnsureSuccessStatusCode();
        var run = await RunAsync(s);
        Assert.Equal(1, run.Disabled);
        Assert.Equal("active", await MembershipStatusAsync(s, (await UserIdAsync(s, inside))!.Value));
        Assert.Equal("disabled", await MembershipStatusAsync(s, (await UserIdAsync(s, outside))!.Value));

        s.Data.AddMember(group, outside);
        Assert.Equal(1, (await RunAsync(s)).Reenabled);
    }

    [Fact]
    public async Task Threshold_breach_stops_the_run_changes_nothing_and_the_same_changes_apply_once_allowed()
    {
        var s = await TenantAsync(new { userScope = new { mode = "all", groups = Array.Empty<string>() }, deletionThresholdPct = 10 });
        var oids = Enumerable.Range(0, 4).Select(_ => Oid()).ToList();
        for (var i = 0; i < oids.Count; i++)
        {
            s.Data.AddUser(oids[i], $"Staff {i}", s.Email($"staff{i}"));
        }
        await RunAsync(s);
        s.Data.UpdateUser(oids[0], enabled: false);
        s.Data.DeleteUser(oids[1]);

        var stopped = await RunAsync(s);
        Assert.Equal("stopped_threshold", stopped.Outcome);
        Assert.Equal(0, await CountAsync(s, "select count(*) from tenancy.tenant_membership where status = 'disabled'"));
        Assert.Equal(4, await CountAsync(s, "select count(*) from identity.directory_user where account_enabled"));
        Assert.True(await world.Database.OutboxCountAsync("DirectorySyncNeedsAttention", s.Id) >= 1);

        (await s.Tenant["admin"].Client.PutAsJsonAsync("/api/v1/tenant/directory", new { deletionThresholdPct = 60 })).EnsureSuccessStatusCode();
        var applied = await RunAsync(s);
        Assert.Equal("succeeded", applied.Outcome);
        Assert.Equal(2, applied.Disabled);
    }

    [Fact]
    public async Task Unreachable_directory_changes_nothing_and_alerts_after_three_failures()
    {
        var s = await TenantAsync();
        var oid = Oid();
        s.Data.AddUser(oid, "Steady User", s.Email("steady"));
        await RunAsync(s);
        s.Data.UpdateUser(oid, enabled: false);

        Graph.Unavailable = true;
        try
        {
            for (var i = 1; i <= 3; i++)
            {
                var failed = await RunAsync(s);
                Assert.Equal("failed", failed.Outcome);
                Assert.Equal(i == 3 ? 1 : 0, await world.Database.OutboxCountAsync("DirectorySyncNeedsAttention", s.Id));
            }
        }
        finally
        {
            Graph.Unavailable = false;
        }
        Assert.Equal("active", await MembershipStatusAsync(s, (await UserIdAsync(s, oid))!.Value));
        Assert.Equal(3, await CountAsync(s, "select count(*) from identity.directory_sync_run where outcome = 'failed' and reason = 'unreachable'"));
        var view = await s.Tenant["admin"].Client.GetFromJsonAsync<JsonElement>("/api/v1/tenant/directory");
        Assert.Equal(3, view.GetProperty("settings").GetProperty("consecutiveFailures").GetInt32());

        Assert.Equal(1, (await RunAsync(s)).Disabled);
        Assert.Equal(0, await CountAsync(s, "select consecutive_failures from identity.directory_connection"));
    }

    [Fact]
    public async Task Selected_groups_are_kept_in_step_get_mapped_roles_and_are_read_only_while_product_groups_stay_untouched()
    {
        var group = Oid();
        var s = await TenantAsync(groupRoleMap: [new { groupId = group, roleKey = "content_editor", scopeType = "tenant", scopeId = (Guid?)null }]);
        (await s.Tenant["admin"].Client.PutAsJsonAsync("/api/v1/tenant/directory", new { syncGroups = new[] { group } })).EnsureSuccessStatusCode();
        var product = await (await s.Tenant["admin"].Client.PostAsJsonAsync("/api/v1/authz/groups", new { key = "comms", name = "Comms team" })).ReadAsync<JsonElement>();
        var productId = product.GetProperty("id").GetGuid();
        (await s.Tenant["admin"].Client.PostAsJsonAsync($"/api/v1/authz/groups/{productId}/members", new { userId = s.Tenant["admin"].Id })).EnsureSuccessStatusCode();

        var (a, b) = (Oid(), Oid());
        s.Data.AddUser(a, "Editor A", s.Email("editor.a"));
        s.Data.AddUser(b, "Editor B", s.Email("editor.b"));
        s.Data.AddGroup(group, "Intranet editors");
        s.Data.AddMember(group, a);
        s.Data.AddMember(group, b);
        var run = await RunAsync(s);
        Assert.Equal(1, run.Groups);

        var key = DirectorySyncService.GroupKey(group);
        var groupId = await world.Factory.InScopeAsync(s.Id, sp => sp.GetRequiredService<IDbSession>().ScalarAsync<Guid>(
            "select id from authz.user_group where key = @k and source = 'idp'", new { k = key }));
        Assert.Equal(2, await CountAsync(s, $"select count(*) from authz.group_member where group_id = '{groupId}'"));
        Assert.Equal(1, await CountAsync(s,
            $"select count(*) from authz.role_assignment ra join authz.role r on r.id = ra.role_id where ra.subject_type = 'group' and ra.subject_id = '{groupId}' and r.key = 'content_editor' and ra.revoked_at is null"));

        s.Data.RemoveMember(group, b);
        await RunAsync(s);
        Assert.Equal(1, await CountAsync(s, $"select count(*) from authz.group_member where group_id = '{groupId}'"));

        var add = await s.Tenant["admin"].Client.PostAsJsonAsync($"/api/v1/authz/groups/{groupId}/members", new { userId = s.Tenant["admin"].Id });
        Assert.Equal(HttpStatusCode.Conflict, add.StatusCode);
        Assert.Contains("conflict.group.directory_owned", await add.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Equal(1, await CountAsync(s, $"select count(*) from authz.group_member where group_id = '{productId}'"));

        // Deselecting the group empties it, so its role stops applying.
        (await s.Tenant["admin"].Client.PutAsJsonAsync("/api/v1/tenant/directory", new { syncGroups = Array.Empty<string>() })).EnsureSuccessStatusCode();
        await RunAsync(s);
        Assert.Equal(0, await CountAsync(s, $"select count(*) from authz.group_member where group_id = '{groupId}'"));
        Assert.Equal(1, await CountAsync(s, $"select count(*) from authz.group_member where group_id = '{productId}'"));
    }

    [Fact]
    public async Task Sync_now_runs_on_the_worker()
    {
        var s = await TenantAsync();
        s.Data.AddUser(Oid(), "Queued User", s.Email("queued"));
        Assert.Equal(HttpStatusCode.Accepted, (await s.Tenant["admin"].Client.PostAsync("/api/v1/tenant/directory/sync", null)).StatusCode);
        await world.Factory.Services.GetRequiredService<OutboxDispatcher>().DispatchBatchAsync(500, CancellationToken.None);
        Assert.Equal(1, await CountAsync(s, "select count(*) from identity.user_account where created_via = 'directory'"));
        var view = await s.Tenant["admin"].Client.GetFromJsonAsync<JsonElement>("/api/v1/tenant/directory");
        Assert.Equal("succeeded", view.GetProperty("runs")[0].GetProperty("outcome").GetString());
        await world.Database.AssertAuditedAsync(s.Id, AuditActions.DirectoryConnectionChanged, s.Connection.ToString());
    }

    [Fact]
    public async Task Deleting_the_connection_lists_synced_users_and_keeps_them()
    {
        var s = await TenantAsync();
        var oid = Oid();
        s.Data.AddUser(oid, "Kept User", s.Email("kept"));
        await RunAsync(s);
        var userId = (await UserIdAsync(s, oid))!.Value;

        var preview = await s.Tenant["admin"].Client.GetFromJsonAsync<JsonElement>($"/api/v1/integrations/connections/{s.Connection}/dependents");
        var dependent = preview.GetProperty("dependents").EnumerateArray().Single();
        Assert.Equal("integrations.dependent.synced_users", dependent.GetProperty("labelKey").GetString());
        Assert.Equal(1, dependent.GetProperty("count").GetInt64());

        Assert.Equal(HttpStatusCode.NoContent, (await s.Tenant["admin"].Client.DeleteAsync($"/api/v1/integrations/connections/{s.Connection}?confirm=true")).StatusCode);
        await world.Factory.Services.GetRequiredService<OutboxDispatcher>().DispatchBatchAsync(500, CancellationToken.None);
        Assert.Equal(0, await CountAsync(s, "select count(*) from identity.directory_user_link"));
        Assert.Equal(0, await CountAsync(s, "select count(*) from identity.directory_connection"));
        Assert.Equal(1, await CountAsync(s, $"select count(*) from identity.user_account where id = '{userId}'"));
    }

    [Fact]
    public async Task Settings_need_identity_config_permission_a_connection_and_valid_values()
    {
        var t = await world.CreateTenantAsync("dirnc-" + Guid.NewGuid().ToString("N")[..8], "workplace_plus");
        var noConnection = await t["admin"].Client.PutAsJsonAsync("/api/v1/tenant/directory", new { scheduleMinutes = 30 });
        Assert.Equal(HttpStatusCode.Conflict, noConnection.StatusCode);
        Assert.Contains("conflict.directory.no_connection", await noConnection.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        var s = await TenantAsync();
        foreach (var bad in new object[]
        {
            new { scheduleMinutes = 1 },
            new { deletionThresholdPct = 0 },
            new { userScope = new { mode = "ous", groups = Array.Empty<string>() } },
            new { userScope = new { mode = "groups", groups = Array.Empty<string>() } },
            new { syncGroups = new[] { "not-a-guid" } },
        })
        {
            Assert.Equal(HttpStatusCode.BadRequest, (await s.Tenant["admin"].Client.PutAsJsonAsync("/api/v1/tenant/directory", bad)).StatusCode);
        }

        foreach (var user in new[] { "employee_gulf", "orgadmin" })
        {
            Assert.Equal(HttpStatusCode.Forbidden, (await world.A[user].Client.GetAsync("/api/v1/tenant/directory")).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await world.A[user].Client.PostAsync("/api/v1/tenant/directory/sync", null)).StatusCode);
        }
    }

    [Fact]
    public async Task The_same_account_in_two_tenants_gives_two_unrelated_users_and_a_run_never_writes_another_tenant()
    {
        var a = await TenantAsync();
        var b = await TenantAsync();
        var oid = Oid();
        a.Data.AddUser(oid, "Twin", a.Email("twin"));
        b.Data.AddUser(oid, "Twin", b.Email("twin"));
        await RunAsync(a);
        var bBefore = await CountAsync(b, "select count(*) from identity.user_account");
        Assert.Equal(0, await CountAsync(b, "select count(*) from identity.directory_user"));

        await RunAsync(b);
        var inA = await UserIdAsync(a, oid);
        var inB = await UserIdAsync(b, oid);
        Assert.NotEqual(inA, inB);
        Assert.Equal(bBefore + 1, await CountAsync(b, "select count(*) from identity.user_account"));
        Assert.Equal(1, await CountAsync(a, "select count(*) from identity.directory_user"));

        // Tenant B cannot run tenant A's connection: the settings row is invisible under B's context.
        var crossed = await world.Factory.InScopeAsync(b.Id, sp => sp.GetRequiredService<DirectorySyncService>().RunAsync(a.Connection, CancellationToken.None));
        Assert.Null(crossed);
    }

    private sealed record Landed(string Location);

    private async Task<Landed> SignInAsync(Synced s, FakeUser user)
    {
        var client = world.Factory.TenantClient(s.Key);
        var start = await client.GetAsync("/api/v1/auth/signin/sso");
        var location = start.Headers.Location!.ToString();
        var state = FakeIdentityProvider.ParseQuery(new Uri(location).Query)["state"];
        var binding = start.Headers.GetValues("Set-Cookie").Select(v => v.Split(';')[0]).First(v => v.StartsWith("tb_sso=", StringComparison.Ordinal));
        var code = world.Factory.Idp.Authenticate(location, user, ClientSecret);
        var callbackClient = world.Factory.TenantClient(s.Key);
        callbackClient.DefaultRequestHeaders.Add("Cookie", binding);
        var done = await callbackClient.GetAsync($"/api/v1/auth/signin/sso/callback?code={code}&state={state}");
        Assert.Equal(HttpStatusCode.Redirect, done.StatusCode);
        return new Landed(done.Headers.Location!.ToString());
    }
}
