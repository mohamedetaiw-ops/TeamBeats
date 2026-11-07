using System.Net;
using Npgsql;
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
