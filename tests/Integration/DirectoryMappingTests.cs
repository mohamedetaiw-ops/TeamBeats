using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TeamBeats.Audit.Contracts;
using TeamBeats.Messaging;
using TeamBeats.Modules.Tenancy.DirectoryMapping;
using TeamBeats.Testing;
using Xunit;

namespace TeamBeats.Integration.Tests;

/// <summary>Spec 002 User Story 5 (R1 for Entra ID): the organization hierarchy mapped from the directory (tasks T020 to T024).</summary>
[Collection(WorldCollection.Name)]
public sealed class DirectoryMappingTests(TenantWorld world)
{
    private static string Oid() => Guid.NewGuid().ToString("D");

    private Task<SyncedTenant> TenantAsync() => DirectoryTestSetup.CreateAsync(world, "map");

    private Task SyncAsync(SyncedTenant s) => DirectoryTestSetup.SyncAsync(world, s);

    private Task<long> CountAsync(SyncedTenant s, string sql) => world.Database.CountInTenantAsync(s.Id, sql);

    private static string Source(SyncedTenant s) => DirectoryMappingService.ExternalSource(s.Connection);

    private static async Task<JsonElement> OkAsync(HttpResponseMessage response)
    {
        Assert.True(response.IsSuccessStatusCode, $"{(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        return await response.ReadAsync<JsonElement>();
    }

    private static async Task<JsonElement> SaveAsync(SyncedTenant s, object mapping) => await OkAsync(await s.Admin.PutAsJsonAsync("/api/v1/org/directory-mapping", mapping));

    private static async Task<JsonElement> PreviewAsync(SyncedTenant s) => await OkAsync(await s.Admin.PostAsync("/api/v1/org/directory-mapping:preview", null));

    private static async Task<JsonElement> ApproveAsync(SyncedTenant s) => await OkAsync(await s.Admin.PostAsync("/api/v1/org/directory-mapping:approve", null));

    private async Task<Guid> UnitAsync(SyncedTenant s, string key) =>
        await world.Factory.InScopeAsync(s.Id, sp => TeamBeats.Persistence.Sql.ScalarAsync<Guid>(sp.GetRequiredService<TeamBeats.Persistence.IDbSession>(),
            "select id from tenancy.org_unit where external_source = @s and external_id = @k", new { s = Source(s), k = key }));

    private async Task<Guid> UserAsync(SyncedTenant s, string oid) =>
        await world.Factory.InScopeAsync(s.Id, sp => TeamBeats.Persistence.Sql.ScalarAsync<Guid>(sp.GetRequiredService<TeamBeats.Persistence.IDbSession>(),
            "select user_id from identity.directory_user_link where directory_id = @d", new { d = oid }));

    private Task<long> PrimaryInAsync(SyncedTenant s, Guid user, Guid unit) =>
        CountAsync(s, $"select count(*) from tenancy.org_membership where user_id = '{user}' and org_unit_id = '{unit}' and is_primary and valid_to is null and owner = 'directory'");

    private static int Count(JsonElement result, string name) => result.GetProperty("run").GetProperty("counts").GetProperty(name).GetInt32();

    /// <summary>Three people in two companies; "Finance" and "finance " are one department.</summary>
    private async Task<(SyncedTenant S, string A, string B, string C)> AttributesTenantAsync(int threshold = 100)
    {
        var s = await TenantAsync();
        var (a, b, c) = (Oid(), Oid(), Oid());
        s.Data.AddUser(a, "Amal", s.Email("amal"));
        s.Data.AddUser(b, "Basel", s.Email("basel"));
        s.Data.AddUser(c, "Carla", s.Email("carla"));
        s.Data.SetOrg(a, "Acme Gulf", "Finance");
        s.Data.SetOrg(b, "acme  gulf", "finance ");
        s.Data.SetOrg(c, "Acme Africa", "HR");
        await SyncAsync(s);
        await SaveAsync(s, new { strategy = "attributes", levels = new[] { "company", "department" }, deletionThresholdPct = threshold });
        return (s, a, b, c);
    }

    [Fact]
    public async Task Preview_changes_nothing_and_approve_creates_directory_units_with_one_primary_membership_each()
    {
        var (s, a, b, c) = await AttributesTenantAsync();

        var preview = await PreviewAsync(s);
        Assert.Equal(4, Count(preview, "create"));
        Assert.Equal(3, Count(preview, "membershipsAdded"));
        Assert.Equal(0, await CountAsync(s, $"select count(*) from tenancy.org_unit where external_source = '{Source(s)}'"));
        Assert.Equal(0, await CountAsync(s, "select count(*) from tenancy.org_membership where owner = 'directory'"));
        await world.Database.AssertAuditedAsync(s.Id, AuditActions.OrgDirectoryMappingChanged, s.Connection.ToString());

        var applied = await ApproveAsync(s);
        Assert.Equal("succeeded", applied.GetProperty("run").GetProperty("outcome").GetString());
        await world.Database.AssertAuditedAsync(s.Id, AuditActions.OrgDirectoryMappingApproved, s.Connection.ToString());
        await world.Database.AssertAuditedAsync(s.Id, AuditActions.OrgDirectoryMappingApplied, s.Connection.ToString());

        Assert.Equal(4, await CountAsync(s, $"select count(*) from tenancy.org_unit where external_source = '{Source(s)}' and owner = 'directory' and owner_connection_id = '{s.Connection}'"));
        var gulf = await UnitAsync(s, "company=acme gulf");
        var finance = await UnitAsync(s, "company=acme gulf/department=finance");
        Assert.Equal(1, await CountAsync(s, $"select count(*) from tenancy.org_unit where id = '{finance}' and parent_id = '{gulf}' and type_key = 'department'"));
        Assert.Equal(1, await CountAsync(s, $"select count(*) from tenancy.org_unit_name where org_unit_id = '{gulf}' and locale = 'en' and name = 'Acme Gulf'"));
        Assert.Equal(1, await PrimaryInAsync(s, await UserAsync(s, a), finance));
        Assert.Equal(1, await PrimaryInAsync(s, await UserAsync(s, b), finance));
        Assert.Equal(1, await PrimaryInAsync(s, await UserAsync(s, c), await UnitAsync(s, "company=acme africa/department=hr")));

        // Idempotent: applying the same directory state again changes nothing.
        await SyncAsync(s);
        Assert.Equal(3, await CountAsync(s, "select count(*) from tenancy.org_membership where owner = 'directory'"));
        Assert.Equal(4, await CountAsync(s, $"select count(*) from tenancy.org_unit where external_source = '{Source(s)}'"));
    }

    [Fact]
    public async Task Directory_owned_units_and_memberships_refuse_product_changes_and_product_units_stay_untouched()
    {
        var (s, a, _, _) = await AttributesTenantAsync();
        var product = await OkAsync(await s.Admin.PostAsJsonAsync("/api/v1/org/units",
            new { parentId = (Guid?)null, typeKey = "department", code = "PMO", names = new Dictionary<string, string> { ["en"] = "Project office" } }));
        var productId = product.GetProperty("unit").GetProperty("id").GetGuid();
        await PreviewAsync(s);
        await ApproveAsync(s);

        var finance = await UnitAsync(s, "company=acme gulf/department=finance");
        var rename = await s.Admin.PatchAsJsonAsync($"/api/v1/org/units/{finance}", new { names = new Dictionary<string, string> { ["en"] = "Money" } });
        Assert.Equal(HttpStatusCode.Conflict, rename.StatusCode);
        Assert.Contains("conflict.org.directory_owned", await rename.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.Conflict, (await s.Admin.PostAsJsonAsync($"/api/v1/org/units/{finance}:move", new { newParentId = productId })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await s.Admin.PostAsJsonAsync($"/api/v1/org/units/{finance}:deactivate", new { childrenAction = "deactivate", membershipsAction = "end" })).StatusCode);

        // Names in other locales stay editable and survive the next sync.
        Assert.Equal(HttpStatusCode.OK, (await s.Admin.PatchAsJsonAsync($"/api/v1/org/units/{finance}", new { names = new Dictionary<string, string> { ["ar"] = "المالية" } })).StatusCode);

        var membership = await world.Factory.InScopeAsync(s.Id, sp => TeamBeats.Persistence.Sql.ScalarAsync<Guid>(sp.GetRequiredService<TeamBeats.Persistence.IDbSession>(),
            "select id from tenancy.org_membership where owner = 'directory' and valid_to is null limit 1"));
        Assert.Equal(HttpStatusCode.Conflict, (await s.Admin.PostAsync($"/api/v1/org/memberships/{membership}:end", null)).StatusCode);

        var units = await s.Admin.GetFromJsonAsync<JsonElement>("/api/v1/org/units");
        Assert.Equal("directory", units.EnumerateArray().First(u => u.GetProperty("id").GetGuid() == finance).GetProperty("owner").GetString());
        Assert.Equal("product", units.EnumerateArray().First(u => u.GetProperty("id").GetGuid() == productId).GetProperty("owner").GetString());

        s.Data.SetOrg(a, "Acme Gulf", "Treasury");
        await SyncAsync(s);
        Assert.Equal(1, await CountAsync(s, $"select count(*) from tenancy.org_unit where id = '{productId}' and status = 'active' and parent_id is null"));
        Assert.Equal(1, await CountAsync(s, $"select count(*) from tenancy.org_unit_name where org_unit_id = '{finance}' and locale = 'ar' and name = 'المالية'"));
    }

    [Fact]
    public async Task Mover_ends_the_old_membership_and_starts_the_new_one_and_an_emptied_unit_is_deactivated()
    {
        var (s, a, b, c) = await AttributesTenantAsync();
        await PreviewAsync(s);
        await ApproveAsync(s);
        var carla = await UserAsync(s, c);
        var hr = await UnitAsync(s, "company=acme africa/department=hr");
        var before = await world.Database.OutboxCountAsync("OrganizationMembershipChanged", s.Id);

        s.Data.SetOrg(c, "Acme Gulf", "Finance");
        await SyncAsync(s);

        var finance = await UnitAsync(s, "company=acme gulf/department=finance");
        Assert.Equal(1, await PrimaryInAsync(s, carla, finance));
        Assert.Equal(1, await CountAsync(s, $"select count(*) from tenancy.org_membership where user_id = '{carla}' and org_unit_id = '{hr}' and valid_to is not null"));
        Assert.True(await world.Database.OutboxCountAsync("OrganizationMembershipChanged", s.Id) >= before);
        Assert.Equal(1, await CountAsync(s, $"select count(*) from tenancy.org_unit where id = '{hr}' and status = 'inactive'"));
        Assert.Equal(1, await CountAsync(s, $"select count(*) from tenancy.org_unit where external_id = 'company=acme africa' and external_source = '{Source(s)}' and status = 'inactive'"));
    }

    [Fact]
    public async Task A_disappearing_unit_that_still_has_product_members_is_flagged_and_left_active()
    {
        var (s, _, _, c) = await AttributesTenantAsync();
        await PreviewAsync(s);
        await ApproveAsync(s);
        var hr = await UnitAsync(s, "company=acme africa/department=hr");
        (await s.Admin.PostAsJsonAsync($"/api/v1/org/units/{hr}/memberships", new { userId = s.Tenant["admin"].Id, type = "secondary" })).EnsureSuccessStatusCode();

        s.Data.SetOrg(c, "Acme Gulf", "Finance");
        await SyncAsync(s);

        Assert.Equal(1, await CountAsync(s, $"select count(*) from tenancy.org_unit where id = '{hr}' and status = 'active'"));
        var view = await s.Admin.GetFromJsonAsync<JsonElement>("/api/v1/org/directory-mapping");
        Assert.True(view.GetProperty("runs")[0].GetProperty("counts").GetProperty("flagged").GetInt32() >= 1);
    }

    [Fact]
    public async Task Alias_rules_merge_values_and_a_value_can_map_to_an_existing_product_unit()
    {
        var s = await TenantAsync();
        var (a, b, c) = (Oid(), Oid(), Oid());
        foreach (var (oid, dept) in new[] { (a, "HR"), (b, "Human Resources"), (c, "Legal") })
        {
            s.Data.AddUser(oid, "Person " + dept, s.Email("p" + oid[..6]));
            s.Data.SetOrg(oid, "Acme", dept);
        }
        await SyncAsync(s);
        var legal = (await OkAsync(await s.Admin.PostAsJsonAsync("/api/v1/org/units",
            new { parentId = (Guid?)null, typeKey = "department", code = "LEGAL", names = new Dictionary<string, string> { ["en"] = "Legal affairs" } })))
            .GetProperty("unit").GetProperty("id").GetGuid();

        await SaveAsync(s, new
        {
            strategy = "attributes", levels = new[] { "company", "department" }, deletionThresholdPct = 100,
            aliases = new[] { new { from = "hr", to = "Human Resources" } },
            valueMap = new[] { new { key = "company=acme/department=legal", unitId = legal } },
        });
        await PreviewAsync(s);
        await ApproveAsync(s);

        var hr = await UnitAsync(s, "company=acme/department=human resources");
        Assert.Equal(1, await PrimaryInAsync(s, await UserAsync(s, a), hr));
        Assert.Equal(1, await PrimaryInAsync(s, await UserAsync(s, b), hr));
        Assert.Equal(1, await PrimaryInAsync(s, await UserAsync(s, c), legal));
        Assert.Equal(0, await CountAsync(s, $"select count(*) from tenancy.org_unit where external_id = 'company=acme/department=legal'"));
        Assert.Equal(1, await CountAsync(s, $"select count(*) from tenancy.org_unit where id = '{legal}' and owner = 'product'"));
    }

    [Fact]
    public async Task Administrative_units_map_one_to_one_follow_renames_and_people_in_several_units_are_unmapped()
    {
        var s = await TenantAsync();
        var (au1, au2) = (Oid(), Oid());
        var (a, b) = (Oid(), Oid());
        s.Data.AddUser(a, "Single", s.Email("single"));
        s.Data.AddUser(b, "Double", s.Email("double"));
        s.Data.AddAdminUnit(au1, "Riyadh office");
        s.Data.AddAdminUnit(au2, "Dubai office");
        s.Data.AddMember(au1, a);
        s.Data.AddMember(au1, b);
        s.Data.AddMember(au2, b);
        await SyncAsync(s);
        await SaveAsync(s, new { strategy = "admin_units", deletionThresholdPct = 100 });
        var preview = await PreviewAsync(s);
        Assert.Contains(preview.GetProperty("diff").GetProperty("unmapped").EnumerateArray(), u => u.GetProperty("reason").GetString() == "ambiguous");
        await ApproveAsync(s);

        var riyadh = await UnitAsync(s, $"admin_unit={au1}");
        Assert.Equal(1, await PrimaryInAsync(s, await UserAsync(s, a), riyadh));
        Assert.Equal(0, await CountAsync(s, $"select count(*) from tenancy.org_membership where user_id = '{await UserAsync(s, b)}' and owner = 'directory'"));

        s.Data.Rename(au1, "Riyadh head office");
        await SyncAsync(s);
        Assert.Equal(riyadh, await UnitAsync(s, $"admin_unit={au1}"));
        Assert.Equal(1, await CountAsync(s, $"select count(*) from tenancy.org_unit_name where org_unit_id = '{riyadh}' and locale = 'en' and name = 'Riyadh head office'"));
    }

    [Fact]
    public async Task Groups_strategy_uses_only_selected_groups()
    {
        var s = await TenantAsync();
        var (g1, g2) = (Oid(), Oid());
        var a = Oid();
        s.Data.AddUser(a, "Grouped", s.Email("grouped"));
        s.Data.AddGroup(g1, "Sales team");
        s.Data.AddGroup(g2, "All staff");
        s.Data.AddMember(g1, a);
        s.Data.AddMember(g2, a);
        await SyncAsync(s);
        var missing = await s.Admin.PutAsJsonAsync("/api/v1/org/directory-mapping", new { strategy = "groups" });
        Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);

        await SaveAsync(s, new { strategy = "groups", groups = new[] { g1 }, deletionThresholdPct = 100 });
        await PreviewAsync(s);
        await ApproveAsync(s);
        Assert.Equal(1, await PrimaryInAsync(s, await UserAsync(s, a), await UnitAsync(s, $"group={g1}")));
        Assert.Equal(0, await CountAsync(s, $"select count(*) from tenancy.org_unit where external_id = 'group={g2}'"));
    }

    [Fact]
    public async Task Threshold_breach_stops_the_apply_before_any_change_and_alerts()
    {
        var (s, a, b, c) = await AttributesTenantAsync(threshold: 10);
        await PreviewAsync(s);
        await ApproveAsync(s);
        var before = await CountAsync(s, "select count(*) from tenancy.org_membership where owner = 'directory' and valid_to is null");

        s.Data.SetOrg(a, null, null);
        s.Data.SetOrg(b, null, null);
        await SyncAsync(s);

        Assert.Equal(before, await CountAsync(s, "select count(*) from tenancy.org_membership where owner = 'directory' and valid_to is null"));
        Assert.Equal(4, await CountAsync(s, $"select count(*) from tenancy.org_unit where external_source = '{Source(s)}' and status = 'active'"));
        var view = await s.Admin.GetFromJsonAsync<JsonElement>("/api/v1/org/directory-mapping");
        Assert.Equal("stopped_threshold", view.GetProperty("runs")[0].GetProperty("outcome").GetString());
        Assert.True(await world.Database.OutboxCountAsync("OrgMappingNeedsAttention", s.Id) >= 1);
    }

    [Fact]
    public async Task Settings_are_validated_need_a_preview_and_a_changed_strategy_needs_approval_again()
    {
        var t = await world.CreateTenantAsync("mapnd-" + Guid.NewGuid().ToString("N")[..8], "workplace_plus");
        var noDirectory = await t["admin"].Client.PutAsJsonAsync("/api/v1/org/directory-mapping", new { strategy = "attributes" });
        Assert.Equal(HttpStatusCode.Conflict, noDirectory.StatusCode);

        var (s, _, _, _) = await AttributesTenantAsync();
        foreach (var bad in new object[]
        {
            new { strategy = "ou_path" },
            new { strategy = "nonsense" },
            new { strategy = "attributes", levels = new[] { "company", "company" } },
            new { strategy = "attributes", levels = new[] { "cost_center" } },
            new { strategy = "attributes", deletionThresholdPct = 0 },
            new { strategy = "attributes", valueMap = new[] { new { key = "company=x", unitId = Guid.NewGuid() } } },
        })
        {
            Assert.Equal(HttpStatusCode.BadRequest, (await s.Admin.PutAsJsonAsync("/api/v1/org/directory-mapping", bad)).StatusCode);
        }
        var unavailable = await s.Admin.PutAsJsonAsync("/api/v1/org/directory-mapping", new { strategy = "ou_path" });
        Assert.Contains("validation.org_mapping.strategy_unavailable", await unavailable.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        var early = await s.Admin.PostAsync("/api/v1/org/directory-mapping:approve", null);
        Assert.Equal(HttpStatusCode.Conflict, early.StatusCode);
        Assert.Contains("conflict.org_mapping.preview_required", await early.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        await PreviewAsync(s);
        await ApproveAsync(s);
        var changed = await SaveAsync(s, new { strategy = "attributes", levels = new[] { "company" } });
        Assert.Equal(JsonValueKind.Null, changed.GetProperty("approvedAt").ValueKind);
    }

    [Fact]
    public async Task Organization_admins_can_read_but_not_change_the_mapping()
    {
        var orgadmin = world.A["orgadmin"].Client;
        Assert.Equal(HttpStatusCode.OK, (await orgadmin.GetAsync("/api/v1/org/directory-mapping")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await orgadmin.PutAsJsonAsync("/api/v1/org/directory-mapping", new { strategy = "attributes" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await orgadmin.PostAsync("/api/v1/org/directory-mapping:preview", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await orgadmin.PostAsync("/api/v1/org/directory-mapping:approve", null)).StatusCode);
    }

    [Fact]
    public async Task Two_tenants_with_the_same_company_names_get_separate_units_and_never_see_each_other()
    {
        var (a, _, _, _) = await AttributesTenantAsync();
        var (b, _, _, _) = await AttributesTenantAsync();
        foreach (var s in new[] { a, b })
        {
            await PreviewAsync(s);
            await ApproveAsync(s);
        }
        Assert.Equal(4, await CountAsync(a, "select count(*) from tenancy.org_unit where owner = 'directory'"));
        Assert.Equal(4, await CountAsync(b, "select count(*) from tenancy.org_unit where owner = 'directory'"));
        var aUnit = await UnitAsync(a, "company=acme gulf");
        Assert.Equal(HttpStatusCode.NotFound, (await b.Admin.GetAsync($"/api/v1/org/units/{aUnit}")).StatusCode);
        var bView = await b.Admin.GetFromJsonAsync<JsonElement>("/api/v1/org/directory-mapping");
        Assert.Equal(b.Connection, bView.GetProperty("mapping").GetProperty("connectionId").GetGuid());
    }

    [Fact]
    public async Task Deleting_the_connection_hands_the_units_back_to_the_product()
    {
        var (s, _, _, _) = await AttributesTenantAsync();
        await PreviewAsync(s);
        await ApproveAsync(s);
        Assert.Equal(HttpStatusCode.NoContent, (await s.Admin.DeleteAsync($"/api/v1/integrations/connections/{s.Connection}?confirm=true")).StatusCode);
        await world.Factory.Services.GetRequiredService<OutboxDispatcher>().DispatchBatchAsync(500, CancellationToken.None);

        Assert.Equal(0, await CountAsync(s, "select count(*) from tenancy.org_unit where owner = 'directory'"));
        Assert.Equal(4, await CountAsync(s, "select count(*) from tenancy.org_unit where owner = 'product' and status = 'active' and external_source is not null"));
        Assert.Equal(0, await CountAsync(s, "select count(*) from tenancy.directory_mapping"));
    }
}
