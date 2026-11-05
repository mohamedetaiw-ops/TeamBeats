using Npgsql;
using TeamBeats.Testing;
using Xunit;

namespace TeamBeats.Security.Tests;

/// <summary>Constitution I and ADR-0002: row-level security is the last line of tenant isolation.</summary>
[Collection(WorldCollection.Name)]
public sealed class DatabaseIsolationTests(TenantWorld world)
{
    /// <summary>Tables with a tenant_id column that are deliberately not under RLS. Each one is justified in docs/architecture/rls-exceptions.md.</summary>
    public static readonly HashSet<string> RlsExceptions =
    [
        "messaging.outbox",
        "messaging.inbox",
        "messaging.dead_letter",
        "identity.verified_domain",
    ];

    private async Task<List<T>> AppQueryAsync<T>(string sql, Func<NpgsqlDataReader, T> map, Guid? tenant = null)
    {
        await using var conn = new NpgsqlConnection(world.Database.AppConnectionString);
        await conn.OpenAsync();
        await using var tx = await conn.BeginTransactionAsync();
        if (tenant is { } t)
        {
            await using var set = new NpgsqlCommand("select set_config('app.tenant_id', @t, true)", conn, tx);
            set.Parameters.AddWithValue("t", t.ToString());
            await set.ExecuteNonQueryAsync();
        }
        await using var cmd = new NpgsqlCommand(sql, conn, tx);
        await using var reader = await cmd.ExecuteReaderAsync();
        var rows = new List<T>();
        while (await reader.ReadAsync())
        {
            rows.Add(map(reader));
        }
        return rows;
    }

    [Fact]
    public async Task Application_role_is_not_superuser_bypassrls_or_table_owner()
    {
        var role = await AppQueryAsync("select rolsuper, rolbypassrls from pg_roles where rolname = current_user", r => (r.GetBoolean(0), r.GetBoolean(1)));
        Assert.Equal((false, false), role.Single());

        var owned = await AppQueryAsync(
            "select count(*) from pg_class c join pg_namespace n on n.oid = c.relnamespace where pg_get_userbyid(c.relowner) = current_user and n.nspname not in ('pg_catalog','information_schema')",
            r => r.GetInt64(0));
        Assert.Equal(0, owned.Single());
    }

    [Fact]
    public async Task Every_tenant_table_has_forced_rls_unless_documented()
    {
        var tables = await AppQueryAsync(
            """
            select n.nspname || '.' || c.relname, c.relrowsecurity, c.relforcerowsecurity
            from pg_class c join pg_namespace n on n.oid = c.relnamespace
            join pg_attribute a on a.attrelid = c.oid and a.attname = 'tenant_id' and not a.attisdropped
            where c.relkind in ('r', 'p') and n.nspname not in ('pg_catalog', 'information_schema')
            order by 1
            """, r => (Name: r.GetString(0), Enabled: r.GetBoolean(1), Forced: r.GetBoolean(2)));

        Assert.NotEmpty(tables);
        var missing = tables.Where(t => !(t.Enabled && t.Forced) && !RlsExceptions.Contains(t.Name)).Select(t => t.Name).ToList();
        Assert.True(missing.Count == 0, "Tables without forced RLS: " + string.Join(", ", missing));

        // An exception that gained RLS should be removed from the list, so the list stays honest.
        var stale = RlsExceptions.Where(e => tables.Any(t => t.Name == e && t.Enabled && t.Forced)).ToList();
        Assert.Empty(stale);
    }

    [Theory]
    [InlineData("identity.user_account")]
    [InlineData("tenancy.org_unit")]
    [InlineData("authz.role_assignment")]
    [InlineData("audit.audit_event")]
    [InlineData("entitlements.subscription")]
    public async Task Without_tenant_context_no_rows_are_visible(string table)
    {
        var count = await AppQueryAsync($"select count(*) from {table}", r => r.GetInt64(0));
        Assert.Equal(0, count.Single());
    }

    [Fact]
    public async Task Tenant_context_shows_only_that_tenant()
    {
        var foreign = await AppQueryAsync("select count(*) from identity.user_account where tenant_id = @b".Replace("@b", $"'{world.B.Id}'"),
            r => r.GetInt64(0), world.A.Id);
        Assert.Equal(0, foreign.Single());
        var own = await AppQueryAsync("select count(*) from identity.user_account", r => r.GetInt64(0), world.A.Id);
        Assert.True(own.Single() > 0);
    }

    [Fact]
    public async Task Writing_a_row_for_another_tenant_is_rejected()
    {
        await using var conn = new NpgsqlConnection(world.Database.AppConnectionString);
        await conn.OpenAsync();
        await using var tx = await conn.BeginTransactionAsync();
        await using (var set = new NpgsqlCommand($"select set_config('app.tenant_id', '{world.A.Id}', true)", conn, tx))
        {
            await set.ExecuteNonQueryAsync();
        }
        await using var insert = new NpgsqlCommand(
            $"insert into authz.user_group (tenant_id, id, key, name, source) values ('{world.B.Id}', gen_random_uuid(), 'smuggled', 'x', 'product')", conn, tx);
        var ex = await Assert.ThrowsAsync<PostgresException>(() => insert.ExecuteNonQueryAsync());
        Assert.Equal("42501", ex.SqlState);
    }

    [Fact]
    public async Task Tenant_context_does_not_leak_across_pooled_connections()
    {
        var builder = new NpgsqlConnectionStringBuilder(world.Database.AppConnectionString) { MaxPoolSize = 1, Pooling = true };
        await using var source = NpgsqlDataSource.Create(builder.ConnectionString);

        await using (var conn = await source.OpenConnectionAsync())
        await using (var tx = await conn.BeginTransactionAsync())
        {
            await using var set = new NpgsqlCommand($"select set_config('app.tenant_id', '{world.A.Id}', true)", conn, tx);
            await set.ExecuteNonQueryAsync();
            await using var read = new NpgsqlCommand("select count(*) from identity.user_account", conn, tx);
            Assert.True((long)(await read.ExecuteScalarAsync())! > 0);
            await tx.CommitAsync();
        }

        await using (var conn = await source.OpenConnectionAsync())
        {
            await using var read = new NpgsqlCommand("select count(*), current_setting('app.tenant_id', true) from identity.user_account", conn);
            await using var reader = await read.ExecuteReaderAsync();
            await reader.ReadAsync();
            Assert.Equal(0, reader.GetInt64(0));
        }
    }
}
