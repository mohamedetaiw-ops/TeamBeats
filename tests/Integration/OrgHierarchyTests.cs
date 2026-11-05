using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using TeamBeats.Audit.Contracts;
using TeamBeats.Testing;
using Xunit;

namespace TeamBeats.Integration.Tests;

/// <summary>Spec 002 organization hierarchy and memberships.</summary>
[Collection(WorldCollection.Name)]
public sealed class OrgHierarchyTests(TenantWorld world)
{
    private static object Unit(string code, Guid? parent, string type = "department") => new
    {
        parentId = parent,
        typeKey = type,
        code,
        names = new Dictionary<string, string> { ["en"] = code, ["ar"] = "وحدة " + code },
    };

    private static async Task<string> ErrorKey(HttpResponseMessage response, string field)
    {
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        return problem.GetProperty("errors").GetProperty(field)[0].GetProperty("key").GetString()!;
    }

    [Fact]
    public async Task Create_unit_is_audited_and_listed_with_localized_names()
    {
        var admin = world.A["admin"].Client;
        var created = await (await admin.PostAsJsonAsync("/api/v1/org/units", Unit("Ops-1", world.A.Unit("Gulf")))).ReadAsync<JsonElement>();
        var id = created.GetProperty("unit").GetProperty("id").GetGuid();
        Assert.Equal(3, created.GetProperty("unit").GetProperty("level").GetInt32());
        await world.Database.AssertAuditedAsync(world.A.Id, AuditActions.OrgUnitCreated, id.ToString());

        var children = await admin.GetFromJsonAsync<JsonElement>($"/api/v1/org/units?parentId={world.A.Unit("Gulf")}");
        Assert.Contains(children.EnumerateArray(), u => u.GetProperty("id").GetGuid() == id);
    }

    [Fact]
    public async Task Parent_from_another_tenant_is_rejected_as_not_found()
    {
        var response = await world.A["admin"].Client.PostAsJsonAsync("/api/v1/org/units", Unit("Cross-1", world.B.Unit("Gulf")));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("validation.org.parent_not_found", await ErrorKey(response, "parentId"));
    }

    [Fact]
    public async Task Unit_of_another_tenant_is_not_found()
    {
        var response = await world.A["admin"].Client.GetAsync($"/api/v1/org/units/{world.B.Unit("Holding")}");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Duplicate_code_conflicts()
    {
        var admin = world.A["admin"].Client;
        (await admin.PostAsJsonAsync("/api/v1/org/units", Unit("Dup-1", world.A.Unit("Holding")))).EnsureSuccessStatusCode();
        var second = await admin.PostAsJsonAsync("/api/v1/org/units", Unit("Dup-1", world.A.Unit("Africa")));
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
    }

    [Fact]
    public async Task Moving_a_unit_under_its_own_descendant_is_a_cycle()
    {
        var admin = world.A["admin"].Client;
        var top = (await (await admin.PostAsJsonAsync("/api/v1/org/units", Unit("Cyc-Top", world.A.Unit("Holding")))).ReadAsync<JsonElement>())
            .GetProperty("unit").GetProperty("id").GetGuid();
        var child = (await (await admin.PostAsJsonAsync("/api/v1/org/units", Unit("Cyc-Child", top))).ReadAsync<JsonElement>())
            .GetProperty("unit").GetProperty("id").GetGuid();

        var response = await admin.PostAsJsonAsync($"/api/v1/org/units/{top}:move", new { newParentId = child });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("validation.org.cycle", await ErrorKey(response, "newParentId"));
    }

    [Fact]
    public async Task Depth_thirteen_is_rejected()
    {
        var admin = world.A["admin"].Client;
        Guid? parent = null;
        for (var level = 1; level <= 12; level++)
        {
            var body = Unit($"Deep-{level}", parent, level == 1 ? "organization" : "department");
            parent = (await (await admin.PostAsJsonAsync("/api/v1/org/units", body)).ReadAsync<JsonElement>()).GetProperty("unit").GetProperty("id").GetGuid();
        }
        var response = await admin.PostAsJsonAsync("/api/v1/org/units", Unit("Deep-13", parent));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("validation.org.max_depth", await ErrorKey(response, "parentId"));
    }

    [Fact]
    public async Task Move_changes_subtree_scope_of_existing_assignments()
    {
        var admin = world.A["admin"].Client;
        var unit = (await (await admin.PostAsJsonAsync("/api/v1/org/units", Unit("Mover-1", world.A.Unit("Africa")))).ReadAsync<JsonElement>())
            .GetProperty("unit").GetProperty("id").GetGuid();

        // The Gulf-subtree publisher cannot see the unit under Africa, then can after it moves under Gulf.
        var publisher = world.A["publisher"];
        var before = await admin.GetFromJsonAsync<JsonElement>(
            $"/api/v1/authz/explain?userId={publisher.Id}&permission=content.page.publish&orgUnitId={unit}");
        Assert.False(before.GetProperty("allowed").GetBoolean());

        (await admin.PostAsJsonAsync($"/api/v1/org/units/{unit}:move", new { newParentId = world.A.Unit("Gulf") })).EnsureSuccessStatusCode();
        await world.Database.AssertAuditedAsync(world.A.Id, AuditActions.OrgUnitMoved, unit.ToString());

        var after = await admin.GetFromJsonAsync<JsonElement>(
            $"/api/v1/authz/explain?userId={publisher.Id}&permission=content.page.publish&orgUnitId={unit}");
        Assert.True(after.GetProperty("allowed").GetBoolean());
    }

    [Fact]
    public async Task Unusual_placement_is_allowed_with_a_warning()
    {
        var created = await (await world.A["admin"].Client.PostAsJsonAsync("/api/v1/org/units",
            Unit("Odd-1", world.A.Unit("GulfHR"), "subsidiary"))).ReadAsync<JsonElement>();
        Assert.Contains(created.GetProperty("warnings").EnumerateArray(), w => w.GetString() == "warning.org.unusual_placement");
    }

    [Fact]
    public async Task Employee_cannot_create_units()
    {
        var response = await world.A["employee_gulf"].Client.PostAsJsonAsync("/api/v1/org/units", Unit("Nope-1", world.A.Unit("Gulf")));
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Org_admin_manages_only_inside_their_subtree()
    {
        var orgadmin = world.A["orgadmin"].Client;
        (await orgadmin.PostAsJsonAsync("/api/v1/org/units", Unit("GulfOps-1", world.A.Unit("Gulf")))).EnsureSuccessStatusCode();
        var outside = await orgadmin.PostAsJsonAsync("/api/v1/org/units", Unit("AfrOps-1", world.A.Unit("Africa")));
        Assert.True(outside.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.NotFound, outside.StatusCode.ToString());
    }

    [Fact]
    public async Task Membership_add_and_end_are_audited()
    {
        var admin = world.A["admin"].Client;
        var user = world.A["employee_holding"];
        var created = await (await admin.PostAsJsonAsync($"/api/v1/org/units/{world.A.Unit("GulfHR")}/memberships",
            new { userId = user.Id, type = "secondary" })).ReadAsync<JsonElement>();
        var membershipId = created.GetProperty("id").GetGuid();
        await world.Database.AssertAuditedAsync(world.A.Id, AuditActions.OrgMembershipAdded);

        (await admin.PostAsync($"/api/v1/org/memberships/{membershipId}:end", null)).EnsureSuccessStatusCode();
        await world.Database.AssertAuditedAsync(world.A.Id, AuditActions.OrgMembershipEnded);
    }

    [Fact]
    public async Task Membership_for_user_of_other_tenant_is_rejected()
    {
        var response = await world.A["admin"].Client.PostAsJsonAsync($"/api/v1/org/units/{world.A.Unit("GulfHR")}/memberships",
            new { userId = world.B["employee_gulf"].Id, type = "secondary" });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Deactivate_requires_a_decision_for_children()
    {
        var admin = world.A["admin"].Client;
        var parent = (await (await admin.PostAsJsonAsync("/api/v1/org/units", Unit("Deact-P", world.A.Unit("Holding")))).ReadAsync<JsonElement>())
            .GetProperty("unit").GetProperty("id").GetGuid();
        await (await admin.PostAsJsonAsync("/api/v1/org/units", Unit("Deact-C", parent))).ReadAsync<JsonElement>();

        var refused = await admin.PostAsJsonAsync($"/api/v1/org/units/{parent}:deactivate", new { });
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        var problem = await refused.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("conflict.org.active_children", problem.GetProperty("detailKey").GetString());
        Assert.Equal(1, problem.GetProperty("activeChildren").GetArrayLength());

        (await admin.PostAsJsonAsync($"/api/v1/org/units/{parent}:deactivate", new { childrenAction = "deactivate", membershipsAction = "end" }))
            .EnsureSuccessStatusCode();
        await world.Database.AssertAuditedAsync(world.A.Id, AuditActions.OrgUnitDeactivated, parent.ToString());

        var child = await admin.PostAsJsonAsync("/api/v1/org/units", Unit("Deact-New", parent));
        Assert.Equal(HttpStatusCode.BadRequest, child.StatusCode);
    }

    [Fact]
    public async Task Rename_is_audited_and_names_are_localized()
    {
        var admin = world.A["admin"].Client;
        var id = (await (await admin.PostAsJsonAsync("/api/v1/org/units", Unit("Ren-1", world.A.Unit("Holding")))).ReadAsync<JsonElement>())
            .GetProperty("unit").GetProperty("id").GetGuid();
        var renamed = await (await admin.PatchAsJsonAsync($"/api/v1/org/units/{id}",
            new { names = new Dictionary<string, string> { ["en"] = "Renamed", ["ar"] = "معاد تسميته" } })).ReadAsync<JsonElement>();
        Assert.Equal("Renamed", renamed.GetProperty("names").GetProperty("en").GetString());
        await world.Database.AssertAuditedAsync(world.A.Id, AuditActions.OrgUnitRenamed, id.ToString());
    }

    [Fact]
    public async Task New_primary_membership_demotes_the_old_one_and_is_audited()
    {
        var user = await world.SeedUserAsync(world.A, "mover_user", "employee");
        await world.AddMembershipAsync(world.A, "mover_user", "GulfHR");
        await world.AddMembershipAsync(world.A, "mover_user", "AfricaFinance", "primary");
        await world.Database.AssertAuditedAsync(world.A.Id, AuditActions.OrgMembershipPrimaryChanged);

        Assert.Equal(1, await world.Database.CountInTenantAsync(world.A.Id,
            "select count(*) from tenancy.org_membership where user_id = @u and is_primary and valid_to is null", ("u", user.Id)));
    }
}
