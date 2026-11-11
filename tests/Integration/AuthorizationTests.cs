using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using TeamBeats.Audit.Contracts;
using TeamBeats.Authorization.Contracts;
using TeamBeats.Testing;
using Xunit;

namespace TeamBeats.Integration.Tests;

/// <summary>Spec 004 authorization: scopes, resource ACLs, inheritance rule, delegation, explain.</summary>
[Collection(WorldCollection.Name)]
public sealed class AuthorizationTests(TenantWorld world)
{
    private async Task<bool> Allowed(TestTenant t, string user, string permission, Guid? unit = null, Guid? doc = null)
    {
        var url = $"/api/v1/authz/explain?userId={t[user].Id}&permission={permission}";
        if (unit is { } u)
        {
            url += $"&orgUnitId={u}";
        }
        if (doc is { } d)
        {
            url += $"&resourceType={TestDocuments.ResourceType}&resourceId={d}";
        }
        var result = await t["admin"].Client.GetFromJsonAsync<JsonElement>(url);
        return result.GetProperty("allowed").GetBoolean();
    }

    [Theory]
    [InlineData("publisher", "Gulf", true)]
    [InlineData("publisher", "GulfFinance", true)]
    [InlineData("publisher", "Africa", false)]
    [InlineData("publisher", "Holding", false)]
    [InlineData("editor_unit", "Gulf", true)]
    [InlineData("editor_unit", "GulfFinance", false)]
    [InlineData("employee_gulf", "GulfFinance", false)]
    [InlineData("security", "AfricaFinance", false)]
    public async Task US1_publish_scope_matrix(string user, string unit, bool expected)
    {
        Assert.Equal(expected, await Allowed(world.A, user, Permissions.ContentPublish, world.A.Unit(unit)));
    }

    [Fact]
    public async Task US1_scope_never_propagates_upwards_or_across_tenants()
    {
        // Assignment in A gives nothing in B, even for the same unit name.
        var bView = await world.B["admin"].Client.GetFromJsonAsync<JsonElement>(
            $"/api/v1/authz/explain?userId={world.B["publisher"].Id}&permission={Permissions.ContentPublish}&orgUnitId={world.B.Unit("Gulf")}");
        Assert.True(bView.GetProperty("allowed").GetBoolean());

        var crossUser = await world.A["admin"].Client.GetFromJsonAsync<JsonElement>(
            $"/api/v1/authz/explain?userId={world.B["publisher"].Id}&permission={Permissions.ContentPublish}&orgUnitId={world.A.Unit("Gulf")}");
        Assert.False(crossUser.GetProperty("allowed").GetBoolean());

        var crossUnit = await world.A["admin"].Client.GetAsync(
            $"/api/v1/authz/explain?userId={world.A["publisher"].Id}&permission={Permissions.ContentPublish}&orgUnitId={world.B.Unit("Gulf")}");
        Assert.Equal(HttpStatusCode.NotFound, crossUnit.StatusCode);
    }

    [Fact]
    public async Task US2_inheritance_rule_lets_ancestor_members_view_restricted_descendant_content()
    {
        // A plain member of Gulf with no role on the content (the publisher's role would already cover it).
        await world.SeedUserAsync(world.A, "gulf_member", "employee");
        await world.AddMembershipAsync(world.A, "gulf_member", "Gulf");
        var doc = world.Factory.Documents.Add(world.A.Id, world.A.Unit("GulfFinance"), ResourceVisibility.Restricted, "finance plan");
        var admin = world.A["admin"].Client;
        (await admin.PutAsJsonAsync($"/api/v1/authz/resources/{TestDocuments.ResourceType}/{doc}/acl",
            new[] { new { subjectType = "org_unit", subjectId = world.A.Unit("GulfFinance"), permission = Permissions.ContentView } })).EnsureSuccessStatusCode();
        await world.Database.AssertAuditedAsync(world.A.Id, AuditActions.ResourcePermissionChanged);

        Assert.True(await Allowed(world.A, "employee_gulf", Permissions.ContentView, doc: doc));
        Assert.False(await Allowed(world.A, "gulf_member", Permissions.ContentView, doc: doc));
        Assert.False(await Allowed(world.A, "employee_africa", Permissions.ContentView, doc: doc));

        (await admin.PutAsJsonAsync("/api/v1/authz/rules",
            new { orgUnitId = world.A.Unit("Gulf"), rule = "members_view_descendant_restricted", enabled = true })).EnsureSuccessStatusCode();
        await world.Database.AssertAuditedAsync(world.A.Id, AuditActions.InheritanceRuleChanged);

        Assert.True(await Allowed(world.A, "gulf_member", Permissions.ContentView, doc: doc));
        Assert.False(await Allowed(world.A, "employee_africa", Permissions.ContentView, doc: doc));

        (await admin.PutAsJsonAsync("/api/v1/authz/rules",
            new { orgUnitId = world.A.Unit("Gulf"), rule = "members_view_descendant_restricted", enabled = false })).EnsureSuccessStatusCode();
        Assert.False(await Allowed(world.A, "gulf_member", Permissions.ContentView, doc: doc));
    }

    [Fact]
    public async Task US3_group_acl_grants_and_membership_change_takes_effect()
    {
        var admin = world.A["admin"].Client;
        var group = await (await admin.PostAsJsonAsync("/api/v1/authz/groups", new { key = "board-readers", name = "Board readers" })).ReadAsync<JsonElement>();
        var groupId = group.GetProperty("id").GetGuid();
        var doc = world.Factory.Documents.Add(world.A.Id, world.A.Unit("Holding"), ResourceVisibility.Restricted, "board pack");
        (await admin.PutAsJsonAsync($"/api/v1/authz/resources/{TestDocuments.ResourceType}/{doc}/acl",
            new[] { new { subjectType = "group", subjectId = groupId, permission = Permissions.ContentView } })).EnsureSuccessStatusCode();

        Assert.False(await Allowed(world.A, "employee_africa", Permissions.ContentView, doc: doc));
        (await admin.PostAsJsonAsync($"/api/v1/authz/groups/{groupId}/members", new { userId = world.A["employee_africa"].Id })).EnsureSuccessStatusCode();
        Assert.True(await Allowed(world.A, "employee_africa", Permissions.ContentView, doc: doc));
    }

    [Fact]
    public async Task Group_cannot_contain_user_of_other_tenant()
    {
        var admin = world.A["admin"].Client;
        var group = await (await admin.PostAsJsonAsync("/api/v1/authz/groups", new { key = "cross-group", name = "Cross" })).ReadAsync<JsonElement>();
        var response = await admin.PostAsJsonAsync($"/api/v1/authz/groups/{group.GetProperty("id").GetGuid()}/members", new { userId = world.B["employee_gulf"].Id });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Revoking_an_assignment_removes_access_and_is_audited()
    {
        var t = world.A;
        await world.SeedUserAsync(t, "temp_publisher", "employee");
        var assignment = await world.AssignAsync(t, "temp_publisher", BuiltInRoles.ContentPublisher, "org_subtree", t.Unit("Africa"));
        await world.Database.AssertAuditedAsync(t.Id, AuditActions.RoleAssigned, assignment.ToString());
        Assert.True(await Allowed(t, "temp_publisher", Permissions.ContentPublish, t.Unit("AfricaFinance")));

        (await t["admin"].Client.DeleteAsync($"/api/v1/authz/assignments/{assignment}")).EnsureSuccessStatusCode();
        await world.Database.AssertAuditedAsync(t.Id, AuditActions.RoleRevoked, assignment.ToString());
        Assert.False(await Allowed(t, "temp_publisher", Permissions.ContentPublish, t.Unit("AfricaFinance")));
    }

    [Fact]
    public async Task Last_tenant_administrator_cannot_be_revoked()
    {
        var t = world.A;
        var assignments = await t["admin"].Client.GetFromJsonAsync<JsonElement>($"/api/v1/authz/assignments?userId={t["admin"].Id}");
        var adminAssignment = assignments.EnumerateArray().First(a => a.GetProperty("roleKey").GetString() == BuiltInRoles.TenantAdministrator);
        var response = await t["admin"].Client.DeleteAsync($"/api/v1/authz/assignments/{adminAssignment.GetProperty("id").GetGuid()}");
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task US5_delegated_org_admin_cannot_assign_outside_subtree_or_escalate()
    {
        var t = world.A;
        // Inside the subtree with a delegable role: allowed.
        var inside = await t["orgadmin"].Client.PostAsJsonAsync("/api/v1/authz/assignments",
            new { subjectType = "user", subjectId = t["employee_gulf"].Id, roleKey = BuiltInRoles.ContentEditor, scopeType = "org_subtree", scopeId = t.Unit("GulfFinance") });
        Assert.Equal(HttpStatusCode.Created, inside.StatusCode);

        // Outside the subtree.
        var outside = await t["orgadmin"].Client.PostAsJsonAsync("/api/v1/authz/assignments",
            new { subjectType = "user", subjectId = t["employee_africa"].Id, roleKey = BuiltInRoles.ContentEditor, scopeType = "org_subtree", scopeId = t.Unit("Africa") });
        Assert.Equal(HttpStatusCode.Forbidden, outside.StatusCode);

        // Tenant-wide scope or a non-delegable role.
        var tenantWide = await t["orgadmin"].Client.PostAsJsonAsync("/api/v1/authz/assignments",
            new { subjectType = "user", subjectId = t["employee_gulf"].Id, roleKey = BuiltInRoles.TenantAdministrator, scopeType = "tenant" });
        Assert.Equal(HttpStatusCode.Forbidden, tenantWide.StatusCode);
    }

    [Fact]
    public async Task Custom_role_cannot_exceed_definer_permissions_and_is_tenant_local()
    {
        var admin = world.A["admin"].Client;
        var created = await admin.PostAsJsonAsync("/api/v1/authz/roles",
            new { key = "news_curator", names = new Dictionary<string, string> { ["en"] = "News curator" }, permissions = new[] { Permissions.ContentEdit, Permissions.ContentView }, delegable = true });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        await world.Database.AssertAuditedAsync(world.A.Id, AuditActions.RoleDefined);

        // The names a custom role was defined with come back with it, so screens can show "News curator", not the key.
        var rolesA = await admin.GetFromJsonAsync<JsonElement>("/api/v1/authz/roles");
        var curator = rolesA.EnumerateArray().Single(r => r.GetProperty("key").GetString() == "news_curator");
        Assert.Equal("News curator", curator.GetProperty("names").GetProperty("en").GetString());

        var rolesB = await world.B["admin"].Client.GetFromJsonAsync<JsonElement>("/api/v1/authz/roles");
        Assert.DoesNotContain(rolesB.EnumerateArray(), r => r.GetProperty("key").GetString() == "news_curator");

        var escalate = await world.A["orgadmin"].Client.PostAsJsonAsync("/api/v1/authz/roles",
            new { key = "sneaky", permissions = new[] { Permissions.TenantSettingsManage }, delegable = false });
        Assert.Equal(HttpStatusCode.Forbidden, escalate.StatusCode);
    }

    [Fact]
    public async Task Contractor_gets_no_default_employee_role_when_policy_removes_it()
    {
        var t = world.B;
        (await t["admin"].Client.PutAsJsonAsync("/api/v1/tenant/membership-policies/contractor", new { defaultEmployeeRole = false })).EnsureSuccessStatusCode();
        await world.SeedUserAsync(t, "contractor_2", "contractor");
        var me = await t["contractor_2"].Client.GetFromJsonAsync<JsonElement>("/api/v1/me/access");
        var roles = me.GetProperty("grants").EnumerateArray().Select(r => r.GetProperty("role").GetString()).ToList();
        Assert.DoesNotContain(BuiltInRoles.Employee, roles);
        var employee = await t["employee_gulf"].Client.GetFromJsonAsync<JsonElement>("/api/v1/me/access");
        Assert.Contains(BuiltInRoles.Employee, employee.GetProperty("grants").EnumerateArray().Select(r => r.GetProperty("role").GetString()));
    }

    [Fact]
    public async Task Membership_policies_are_readable_and_guests_never_get_the_default_employee_role()
    {
        var t = world.B;
        var policies = await t["admin"].Client.GetFromJsonAsync<JsonElement>("/api/v1/tenant/membership-policies");
        var guest = policies.EnumerateArray().Single(p => p.GetProperty("membershipType").GetString() == "guest");
        Assert.False(guest.GetProperty("defaultEmployeeRole").GetBoolean());
        Assert.True(guest.GetProperty("fixed").GetBoolean());
        Assert.Contains(policies.EnumerateArray(), p => p.GetProperty("membershipType").GetString() == "employee" && p.GetProperty("defaultEmployeeRole").GetBoolean());

        // The tenant cannot turn it on for guests, and an employee cannot read the policies at all.
        var turnOn = await t["admin"].Client.PutAsJsonAsync("/api/v1/tenant/membership-policies/guest", new { defaultEmployeeRole = true });
        Assert.Equal(HttpStatusCode.BadRequest, turnOn.StatusCode);
        var employeeRead = await t["employee_gulf"].Client.GetAsync("/api/v1/tenant/membership-policies");
        Assert.Equal(HttpStatusCode.Forbidden, employeeRead.StatusCode);

        // Even with the seeded policy row still on, a guest holds no Employee grant.
        await world.SeedUserAsync(t, "guest_1", "guest");
        var me = await t["guest_1"].Client.GetFromJsonAsync<JsonElement>("/api/v1/me/access");
        Assert.DoesNotContain(BuiltInRoles.Employee, me.GetProperty("grants").EnumerateArray().Select(r => r.GetProperty("role").GetString()));

        // Reading back reflects a change.
        (await t["admin"].Client.PutAsJsonAsync("/api/v1/tenant/membership-policies/contractor", new { defaultEmployeeRole = false })).EnsureSuccessStatusCode();
        policies = await t["admin"].Client.GetFromJsonAsync<JsonElement>("/api/v1/tenant/membership-policies");
        Assert.False(policies.EnumerateArray().Single(p => p.GetProperty("membershipType").GetString() == "contractor").GetProperty("defaultEmployeeRole").GetBoolean());
    }

    [Fact]
    public async Task US6_explain_returns_reasons_and_is_denied_to_employees()
    {
        var t = world.A;
        var result = await t["admin"].Client.GetFromJsonAsync<JsonElement>(
            $"/api/v1/authz/explain?userId={t["publisher"].Id}&permission={Permissions.ContentPublish}&orgUnitId={t.Unit("GulfHR")}");
        var reason = result.GetProperty("reasons")[0];
        Assert.Equal("role_assignment", reason.GetProperty("kind").GetString());
        Assert.Equal(BuiltInRoles.ContentPublisher, reason.GetProperty("roleKey").GetString());

        var denied = await t["employee_gulf"].Client.GetAsync(
            $"/api/v1/authz/explain?userId={t["publisher"].Id}&permission={Permissions.ContentPublish}");
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
    }

    [Fact]
    public async Task Acl_on_resource_of_other_tenant_is_not_found()
    {
        var doc = world.Factory.Documents.Add(world.B.Id, world.B.Unit("Gulf"), ResourceVisibility.Restricted, "b doc");
        var response = await world.A["admin"].Client.GetAsync($"/api/v1/authz/resources/{TestDocuments.ResourceType}/{doc}/acl");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
