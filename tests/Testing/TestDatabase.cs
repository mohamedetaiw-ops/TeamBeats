using Npgsql;
using TeamBeats.Api;
using TeamBeats.Persistence;

namespace TeamBeats.Testing;

/// <summary>
/// A fresh PostgreSQL database per test assembly, with the real roles (tb_owner, tb_app) and RLS on.
/// Connects with TEAMBEATS_TEST_PG_ADMIN (a superuser used only to create roles and the database).
/// CI runs this against a PostgreSQL 18 started by .github/actions/windows-postgres.
/// </summary>
public sealed class TestDatabase : IAsyncDisposable
{
    private const string OwnerPassword = "tb_owner_test_only";
    private const string AppPassword = "tb_app_test_only";

    private TestDatabase(string adminConnection, string name)
    {
        AdminConnection = adminConnection;
        Name = name;
        var host = new NpgsqlConnectionStringBuilder(adminConnection);
        OwnerConnectionString = new NpgsqlConnectionStringBuilder
        {
            Host = host.Host, Port = host.Port, Database = name, Username = "tb_owner", Password = OwnerPassword, Pooling = false,
        }.ConnectionString;
        AppConnectionString = new NpgsqlConnectionStringBuilder
        {
            Host = host.Host, Port = host.Port, Database = name, Username = "tb_app", Password = AppPassword, MaxPoolSize = 50,
        }.ConnectionString;
    }

    public string AdminConnection { get; }
    public string Name { get; }
    public string OwnerConnectionString { get; }
    public string AppConnectionString { get; }

    public static async Task<TestDatabase> CreateAsync()
    {
        var admin = Environment.GetEnvironmentVariable("TEAMBEATS_TEST_PG_ADMIN")
                    ?? "Host=localhost;Port=5432;Username=postgres;Password=postgres";
        var db = new TestDatabase(admin, "tb_test_" + Guid.NewGuid().ToString("N")[..12]);

        await using (var conn = new NpgsqlConnection(admin))
        {
            await conn.OpenAsync();
            await Exec(conn, "select pg_advisory_lock(424242)");
            try
            {
                await Exec(conn, $"""
                    do $$ begin
                      if not exists (select 1 from pg_roles where rolname = 'tb_owner') then
                        create role tb_owner login password '{OwnerPassword}' nosuperuser nobypassrls nocreaterole;
                      else alter role tb_owner password '{OwnerPassword}'; end if;
                      if not exists (select 1 from pg_roles where rolname = 'tb_app') then
                        create role tb_app login password '{AppPassword}' nosuperuser nobypassrls nocreaterole nocreatedb;
                      else alter role tb_app password '{AppPassword}'; end if;
                      if not exists (select 1 from pg_roles where rolname = 'tb_operator') then
                        create role tb_operator nologin nosuperuser nobypassrls;
                      end if;
                    end $$;
                    """);
            }
            finally
            {
                await Exec(conn, "select pg_advisory_unlock(424242)");
            }
            await Exec(conn, $"create database {db.Name} owner tb_owner");
            await Exec(conn, $"grant connect on database {db.Name} to tb_app");
        }

        await using (var conn = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(admin) { Database = db.Name }.ConnectionString))
        {
            await conn.OpenAsync();
            await Exec(conn, "revoke all on schema public from public");
        }

        await MigrationRunner.RunAsync(db.OwnerConnectionString, TeamBeatsModules.Migrations);
        return db;
    }

    /// <summary>Runs SQL as the table owner (for setup and tamper simulations only).</summary>
    public async Task<T?> OwnerScalarAsync<T>(string sql, params (string Name, object Value)[] args)
    {
        await using var conn = new NpgsqlConnection(OwnerConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        foreach (var (n, v) in args)
        {
            cmd.Parameters.AddWithValue(n, v);
        }
        var result = await cmd.ExecuteScalarAsync();
        return result is null or DBNull ? default : (T)result;
    }

    public async Task<int> OwnerExecAsync(string sql, params (string Name, object Value)[] args)
    {
        await using var conn = new NpgsqlConnection(OwnerConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        foreach (var (n, v) in args)
        {
            cmd.Parameters.AddWithValue(n, v);
        }
        return await cmd.ExecuteNonQueryAsync();
    }

    private static async Task Exec(NpgsqlConnection conn, string sql)
    {
        await using var cmd = new NpgsqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync();
    }

    public async ValueTask DisposeAsync()
    {
        NpgsqlConnection.ClearAllPools();
        await using var conn = new NpgsqlConnection(AdminConnection);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand($"drop database if exists {Name} with (force)", conn);
        await cmd.ExecuteNonQueryAsync();
    }
}
