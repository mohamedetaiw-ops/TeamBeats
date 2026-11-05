using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Npgsql;

namespace TeamBeats.Persistence;

/// <summary>A module's ordered SQL migrations, embedded as resources named <c>Migrations/NNNN_name.sql</c>.</summary>
public sealed record MigrationSource(string Module, Assembly Assembly)
{
    public IEnumerable<(string Name, string Sql)> Scripts()
    {
        var prefix = Assembly.GetName().Name + ".Migrations.";
        foreach (var resource in Assembly.GetManifestResourceNames()
                     .Where(n => n.StartsWith(prefix, StringComparison.Ordinal) && n.EndsWith(".sql", StringComparison.Ordinal))
                     .OrderBy(n => n, StringComparer.Ordinal))
        {
            using var stream = Assembly.GetManifestResourceStream(resource)!;
            using var reader = new StreamReader(stream);
            yield return (resource[prefix.Length..], reader.ReadToEnd());
        }
    }
}

/// <summary>
/// Applies module migrations in order, as <c>tb_owner</c>. Applied scripts are recorded with a checksum;
/// editing an applied script fails the run so migrations stay append-only.
/// </summary>
public static class MigrationRunner
{
    public static readonly MigrationSource Platform = new("platform", typeof(MigrationRunner).Assembly);

    public static async Task RunAsync(string ownerConnectionString, IEnumerable<MigrationSource> sources, CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(ownerConnectionString);
        await conn.OpenAsync(ct);

        await using (var lockCmd = new NpgsqlCommand("select pg_advisory_lock(727274)", conn))
        {
            await lockCmd.ExecuteNonQueryAsync(ct);
        }

        try
        {
            await using (var init = new NpgsqlCommand(
                """
                create schema if not exists platform;
                create table if not exists platform.schema_migrations (
                  module text not null,
                  script text not null,
                  checksum text not null,
                  applied_at timestamptz not null default now(),
                  primary key (module, script));
                """, conn))
            {
                await init.ExecuteNonQueryAsync(ct);
            }

            foreach (var source in sources)
            {
                foreach (var (name, sql) in source.Scripts())
                {
                    var checksum = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sql.ReplaceLineEndings("\n"))));
                    await using (var check = new NpgsqlCommand("select checksum from platform.schema_migrations where module = @m and script = @s", conn))
                    {
                        check.Parameters.AddWithValue("m", source.Module);
                        check.Parameters.AddWithValue("s", name);
                        var existing = (string?)await check.ExecuteScalarAsync(ct);
                        if (existing is not null)
                        {
                            if (existing != checksum)
                            {
                                throw new InvalidOperationException($"Applied migration {source.Module}/{name} was modified. Add a new migration instead.");
                            }
                            continue;
                        }
                    }

                    await using var tx = await conn.BeginTransactionAsync(ct);
                    await using (var apply = new NpgsqlCommand(sql, conn, tx))
                    {
                        await apply.ExecuteNonQueryAsync(ct);
                    }
                    await using (var record = new NpgsqlCommand("insert into platform.schema_migrations(module, script, checksum) values (@m, @s, @c)", conn, tx))
                    {
                        record.Parameters.AddWithValue("m", source.Module);
                        record.Parameters.AddWithValue("s", name);
                        record.Parameters.AddWithValue("c", checksum);
                        await record.ExecuteNonQueryAsync(ct);
                    }
                    await tx.CommitAsync(ct);
                }
            }
        }
        finally
        {
            await using var unlock = new NpgsqlCommand("select pg_advisory_unlock(727274)", conn);
            await unlock.ExecuteNonQueryAsync(ct);
        }
    }
}
