using System.Net;
using System.Net.Http.Json;
using Npgsql;
using TeamBeats.Authorization.Contracts;
using TeamBeats.Modules.Authorization;
using TeamBeats.Testing;
using Xunit;

namespace TeamBeats.Integration.Tests;

/// <summary>
/// Authorization migration 0007: tenants that existed before the page, theme, shell and personalization permissions were
/// added get them, with their tenant set, because row-level security hid every row from the earlier backfills.
/// </summary>
[Collection(WorldCollection.Name)]
public sealed class PermissionBackfillTests(TenantWorld world)
{
    [Fact]
    public async Task Backfill_gives_an_older_tenant_the_built_in_permissions_it_lacks_and_leaves_other_tenants_alone()
    {
        var old = await world.CreateTenantAsync("bf-" + Guid.NewGuid().ToString("N")[..8], "workplace");
        var admin = old["admin"].Client;
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync("/api/v1/experience/shell/definition")).StatusCode);

        // Make it look like a tenant created before migrations 0003 to 0006.
        await world.Database.OwnerExecAsync(
            """
            select set_config('app.tenant_id', @t, false);
            delete from authz.role_permission where permission_key in ('shell.edit', 'page.publish', 'theme.edit', 'personal_layout.edit');
            delete from authz.role_permission where role_id in (select id from authz.role where key in ('page_editor', 'brand_manager'));
            delete from authz.role where key in ('page_editor', 'brand_manager');
            update authz.grants_version set version = version + 1;
            """, ("t", old.Id.ToString()));
        Assert.Equal(HttpStatusCode.Forbidden, (await admin.GetAsync("/api/v1/experience/shell/definition")).StatusCode);
        var before = await CountAsync(world.A.Id);

        var script = AuthorizationModule.Migrations.Scripts().Single(s => s.Name.StartsWith("0007_", StringComparison.Ordinal)).Sql;
        await world.Database.OwnerExecAsync(script);
        await world.Database.OwnerExecAsync(script);

        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync("/api/v1/experience/shell/definition")).StatusCode);
        Assert.Equal(2, await CountAsync(old.Id, "select count(*) from authz.role where key in ('page_editor', 'brand_manager')"));
        Assert.Equal(before, await CountAsync(world.A.Id));
        Assert.True(await world.Database.OwnerScalarAsync<bool>(
            "select relforcerowsecurity from pg_class where oid = 'authz.role'::regclass"));
    }

    [Fact]
    public async Task Notification_backfill_gives_an_older_tenants_administrators_notification_settings()
    {
        var old = await world.CreateTenantAsync("bfn-" + Guid.NewGuid().ToString("N")[..8], "workplace");
        var admin = old["admin"].Client;
        await world.Database.OwnerExecAsync(
            """
            select set_config('app.tenant_id', @t, false);
            delete from authz.role_permission where permission_key = 'notifications.manage';
            update authz.grants_version set version = version + 1;
            """, ("t", old.Id.ToString()));
        Assert.Equal(HttpStatusCode.Forbidden, (await admin.GetAsync("/api/v1/notifications/settings")).StatusCode);
        var before = await CountAsync(world.A.Id);

        var script = AuthorizationModule.Migrations.Scripts().Single(s => s.Name.StartsWith("0008_", StringComparison.Ordinal)).Sql;
        await world.Database.OwnerExecAsync(script);
        await world.Database.OwnerExecAsync(script);

        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync("/api/v1/notifications/settings")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await (await world.SeedUserAsync(old, "employee", TeamBeats.Modules.Tenancy.Contracts.MembershipTypes.Employee))
            .Client.GetAsync("/api/v1/notifications/settings")).StatusCode);
        Assert.Equal(before, await CountAsync(world.A.Id));
    }

    [Fact]
    public async Task Calendar_backfill_adds_the_HR_officer_role_and_calendar_permission_to_an_older_tenant()
    {
        var old = await world.CreateTenantAsync("bfh-" + Guid.NewGuid().ToString("N")[..8], "workplace");
        await world.Database.OwnerExecAsync(
            """
            select set_config('app.tenant_id', @t, false);
            delete from authz.role_permission where permission_key = 'hr.calendar_manage';
            delete from authz.role_permission where role_id in (select id from authz.role where key = 'hr_officer');
            delete from authz.role where key = 'hr_officer';
            update authz.grants_version set version = version + 1;
            """, ("t", old.Id.ToString()));
        Assert.Equal(HttpStatusCode.Forbidden, (await old["admin"].Client.GetAsync("/api/v1/hr/calendars")).StatusCode);

        var script = AuthorizationModule.Migrations.Scripts().Single(s => s.Name.StartsWith("0009_", StringComparison.Ordinal)).Sql;
        await world.Database.OwnerExecAsync(script);
        await world.Database.OwnerExecAsync(script);

        Assert.Equal(HttpStatusCode.OK, (await old["admin"].Client.GetAsync("/api/v1/hr/calendars")).StatusCode);
        Assert.Equal(1, await CountAsync(old.Id, "select count(*) from authz.role where key = 'hr_officer'"));
    }

    [Fact]
    public async Task Policy_backfill_adds_the_compliance_officer_role_and_policy_permissions_to_an_older_tenant()
    {
        var old = await world.CreateTenantAsync("bfpo-" + Guid.NewGuid().ToString("N")[..8], "workplace");
        await world.Database.OwnerExecAsync(
            """
            select set_config('app.tenant_id', @t, false);
            delete from authz.role_permission where permission_key like 'policy.%';
            delete from authz.role_permission where role_id in (select id from authz.role where key = 'compliance_officer');
            delete from authz.role where key = 'compliance_officer';
            update authz.grants_version set version = version + 1;
            """, ("t", old.Id.ToString()));
        var create = new { code = "BF-1", names = new Dictionary<string, string> { ["en"] = "Code of conduct" } };
        Assert.Equal(HttpStatusCode.Forbidden, (await old["admin"].Client.PostAsJsonAsync("/api/v1/policies", create)).StatusCode);

        var script = AuthorizationModule.Migrations.Scripts().Single(s => s.Name.StartsWith("0015_", StringComparison.Ordinal)).Sql;
        await world.Database.OwnerExecAsync(script);
        await world.Database.OwnerExecAsync(script);

        Assert.Equal(HttpStatusCode.Created, (await old["admin"].Client.PostAsJsonAsync("/api/v1/policies", create)).StatusCode);
        Assert.Equal(1, await CountAsync(old.Id, "select count(*) from authz.role where key = 'compliance_officer'"));
        Assert.Equal(4, await CountAsync(old.Id,
            "select count(*) from authz.role_permission p join authz.role r on r.id = p.role_id where r.key = 'compliance_officer'"));
    }

    [Fact]
    public async Task People_backfill_gives_an_older_tenants_employees_and_administrators_the_people_permissions()
    {
        var old = await world.CreateTenantAsync("bfp-" + Guid.NewGuid().ToString("N")[..8], "workplace");
        var employee = await world.SeedUserAsync(old, "employee", TeamBeats.Modules.Tenancy.Contracts.MembershipTypes.Employee);
        await world.Database.OwnerExecAsync(
            """
            select set_config('app.tenant_id', @t, false);
            delete from authz.role_permission where permission_key like 'people.%';
            update authz.grants_version set version = version + 1;
            """, ("t", old.Id.ToString()));
        Assert.Equal(HttpStatusCode.Forbidden, (await employee.Client.GetAsync("/api/v1/people")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await old["admin"].Client.GetAsync("/api/v1/people/policy")).StatusCode);

        var script = AuthorizationModule.Migrations.Scripts().Single(s => s.Name.StartsWith("0010_", StringComparison.Ordinal)).Sql;
        await world.Database.OwnerExecAsync(script);
        await world.Database.OwnerExecAsync(script);

        Assert.Equal(HttpStatusCode.OK, (await employee.Client.GetAsync("/api/v1/people")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await old["admin"].Client.GetAsync("/api/v1/people/policy")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await employee.Client.GetAsync("/api/v1/people/policy")).StatusCode);
    }

    [Fact]
    public async Task Ai_backfill_gives_an_older_tenants_security_officer_the_model_policy_view()
    {
        var old = await world.CreateTenantAsync("bfai-" + Guid.NewGuid().ToString("N")[..8], "workplace");
        await world.SeedUserAsync(old, "sec", TeamBeats.Modules.Tenancy.Contracts.MembershipTypes.Employee);
        await world.AssignAsync(old, "sec", BuiltInRoles.SecurityOfficer, "tenant", null);
        await world.Database.OwnerExecAsync(
            """
            select set_config('app.tenant_id', @t, false);
            delete from authz.role_permission where permission_key = 'ai.policy_view';
            update authz.grants_version set version = version + 1;
            """, ("t", old.Id.ToString()));
        Assert.Equal(HttpStatusCode.Forbidden, (await old["sec"].Client.GetAsync("/api/v1/ai/policy")).StatusCode);

        var script = AuthorizationModule.Migrations.Scripts().Single(s => s.Name.StartsWith("0016_", StringComparison.Ordinal)).Sql;
        await world.Database.OwnerExecAsync(script);
        await world.Database.OwnerExecAsync(script);

        Assert.Equal(HttpStatusCode.OK, (await old["sec"].Client.GetAsync("/api/v1/ai/policy")).StatusCode);
        Assert.Equal(3, await CountAsync(old.Id, "select count(*) from authz.role_permission where permission_key = 'ai.policy_view'"));
    }

    [Fact]
    public async Task Eval_backfill_adds_the_evaluation_reviewer_role_and_eval_permissions_to_an_older_tenant()
    {
        var old = await world.CreateTenantAsync("bfev-" + Guid.NewGuid().ToString("N")[..8], "workplace");
        await world.SeedUserAsync(old, "sec", TeamBeats.Modules.Tenancy.Contracts.MembershipTypes.Employee);
        await world.AssignAsync(old, "sec", BuiltInRoles.SecurityOfficer, "tenant", null);
        await world.Database.OwnerExecAsync(
            """
            select set_config('app.tenant_id', @t, false);
            delete from authz.role_permission where permission_key like 'eval.%';
            delete from authz.role_permission where role_id in (select id from authz.role where key = 'evaluation_reviewer');
            delete from authz.role where key = 'evaluation_reviewer';
            update authz.grants_version set version = version + 1;
            """, ("t", old.Id.ToString()));
        Assert.Equal(HttpStatusCode.Forbidden, (await old["sec"].Client.GetAsync("/api/v1/evaluation/settings")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await old["admin"].Client.PutAsJsonAsync("/api/v1/evaluation/settings", new { version = 0, metrics = Array.Empty<object>() })).StatusCode);

        var script = AuthorizationModule.Migrations.Scripts().Single(s => s.Name.StartsWith("0019_", StringComparison.Ordinal)).Sql;
        await world.Database.OwnerExecAsync(script);
        await world.Database.OwnerExecAsync(script);

        Assert.Equal(HttpStatusCode.OK, (await old["sec"].Client.GetAsync("/api/v1/evaluation/settings")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await old["admin"].Client.GetAsync("/api/v1/evaluation/settings")).StatusCode);
        Assert.Equal(1, await CountAsync(old.Id, "select count(*) from authz.role where key = 'evaluation_reviewer'"));
        Assert.Equal(2, await CountAsync(old.Id,
            "select count(*) from authz.role_permission p join authz.role r on r.id = p.role_id where r.key = 'evaluation_reviewer'"));
    }

    [Fact]
    public async Task Finance_backfill_adds_the_finance_officer_role_to_an_older_tenant()
    {
        var old = await world.CreateTenantAsync("bffi-" + Guid.NewGuid().ToString("N")[..8], "workplace");
        await world.Database.OwnerExecAsync(
            """
            select set_config('app.tenant_id', @t, false);
            delete from authz.role_permission where permission_key = 'finance.process';
            delete from authz.role_permission where role_id in (select id from authz.role where key = 'finance_officer');
            delete from authz.role where key = 'finance_officer';
            update authz.grants_version set version = version + 1;
            """, ("t", old.Id.ToString()));

        var script = AuthorizationModule.Migrations.Scripts().Single(s => s.Name.StartsWith("0024_", StringComparison.Ordinal)).Sql;
        await world.Database.OwnerExecAsync(script);
        await world.Database.OwnerExecAsync(script);

        Assert.Equal(1, await CountAsync(old.Id, "select count(*) from authz.role where key = 'finance_officer'"));
        Assert.Equal(3, await CountAsync(old.Id,
            "select count(*) from authz.role_permission p join authz.role r on r.id = p.role_id where r.key = 'finance_officer'"));
        Assert.Equal(2, await CountAsync(old.Id, "select count(*) from authz.role_permission where permission_key = 'finance.process'"));
    }

    /// <summary>Counts as the table owner with one tenant set, on one connection, as row-level security requires.</summary>
    private async Task<long> CountAsync(Guid tenant, string sql = "select count(*) from authz.role_permission")
    {
        await using var conn = new NpgsqlConnection(world.Database.OwnerConnectionString);
        await conn.OpenAsync();
        await using (var set = new NpgsqlCommand("select set_config('app.tenant_id', @t, false)", conn))
        {
            set.Parameters.AddWithValue("t", tenant.ToString());
            await set.ExecuteNonQueryAsync();
        }
        await using var count = new NpgsqlCommand(sql, conn);
        return (long)(await count.ExecuteScalarAsync())!;
    }
}
