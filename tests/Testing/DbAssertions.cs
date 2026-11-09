using Npgsql;
using Xunit;

namespace TeamBeats.Testing;

/// <summary>Direct database checks. Owner connections are subject to FORCE RLS too, so tenant reads set context first.</summary>
public static class DbAssertions
{
    public static async Task<long> CountInTenantAsync(this TestDatabase db, Guid tenantId, string sql, params (string Name, object Value)[] args) =>
        Convert.ToInt64(await db.ScalarInTenantAsync<object>(tenantId, sql, args));

    /// <summary>One value read as the owner inside the tenant, so forced row security lets the row through.</summary>
    public static async Task<T?> ScalarInTenantAsync<T>(this TestDatabase db, Guid tenantId, string sql, params (string Name, object Value)[] args)
    {
        await using var conn = new NpgsqlConnection(db.OwnerConnectionString);
        await conn.OpenAsync();
        await using var tx = await conn.BeginTransactionAsync();
        await using (var set = new NpgsqlCommand("select set_config('app.tenant_id', @t, true)", conn, tx))
        {
            set.Parameters.AddWithValue("t", tenantId.ToString());
            await set.ExecuteNonQueryAsync();
        }
        await using var cmd = new NpgsqlCommand(sql, conn, tx);
        foreach (var (n, v) in args)
        {
            cmd.Parameters.AddWithValue(n, v);
        }
        var result = await cmd.ExecuteScalarAsync();
        return result is null or DBNull ? default : (T)result;
    }

    /// <summary>Runs a statement as the schema owner inside a tenant context (simulates direct database access, for tamper tests).</summary>
    public static async Task<int> OwnerExecInTenantAsync(this TestDatabase db, Guid tenantId, string sql, params (string Name, object Value)[] args)
    {
        await using var conn = new NpgsqlConnection(db.OwnerConnectionString);
        await conn.OpenAsync();
        await using var tx = await conn.BeginTransactionAsync();
        await using (var set = new NpgsqlCommand("select set_config('app.tenant_id', @t, true)", conn, tx))
        {
            set.Parameters.AddWithValue("t", tenantId.ToString());
            await set.ExecuteNonQueryAsync();
        }
        await using var cmd = new NpgsqlCommand(sql, conn, tx);
        foreach (var (n, v) in args)
        {
            cmd.Parameters.AddWithValue(n, v);
        }
        var rows = await cmd.ExecuteNonQueryAsync();
        await tx.CommitAsync();
        return rows;
    }

    /// <summary>Runs a statement as the application role inside a tenant context.</summary>
    public static async Task<int> AppExecInTenantAsync(this TestDatabase db, Guid tenantId, string sql)
    {
        await using var conn = new NpgsqlConnection(db.AppConnectionString);
        await conn.OpenAsync();
        await using var tx = await conn.BeginTransactionAsync();
        await using (var set = new NpgsqlCommand("select set_config('app.tenant_id', @t, true)", conn, tx))
        {
            set.Parameters.AddWithValue("t", tenantId.ToString());
            await set.ExecuteNonQueryAsync();
        }
        await using var cmd = new NpgsqlCommand(sql, conn, tx);
        var rows = await cmd.ExecuteNonQueryAsync();
        await tx.CommitAsync();
        return rows;
    }

    public static Task<long> AuditCountAsync(this TestDatabase db, Guid tenantId, string action, string? targetId = null) =>
        db.CountInTenantAsync(tenantId,
            "select count(*) from audit.audit_event where action = @a and (@id = '' or target_id = @id)",
            ("a", action), ("id", targetId ?? string.Empty));

    public static async Task AssertAuditedAsync(this TestDatabase db, Guid tenantId, string action, string? targetId = null)
    {
        var count = await db.AuditCountAsync(tenantId, action, targetId);
        Assert.True(count > 0, $"Expected audit event '{action}' for tenant {tenantId}{(targetId is null ? string.Empty : $" target {targetId}")}.");
    }

    public static async Task<long> PlatformAuditCountAsync(this TestDatabase db, string action, Guid subjectTenant)
    {
        await using var conn = new NpgsqlConnection(db.OwnerConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand("select count(*) from audit.platform_audit_event where action = @a and subject_tenant_id = @t", conn);
        cmd.Parameters.AddWithValue("a", action);
        cmd.Parameters.AddWithValue("t", subjectTenant);
        return Convert.ToInt64(await cmd.ExecuteScalarAsync());
    }

    public static async Task<long> OutboxCountAsync(this TestDatabase db, string messageType, Guid? tenantId)
    {
        await using var conn = new NpgsqlConnection(db.OwnerConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "select count(*) from messaging.outbox where message_type = @m and tenant_id is not distinct from @t", conn);
        cmd.Parameters.AddWithValue("m", messageType);
        cmd.Parameters.Add(new NpgsqlParameter("t", NpgsqlTypes.NpgsqlDbType.Uuid) { Value = (object?)tenantId ?? DBNull.Value });
        return Convert.ToInt64(await cmd.ExecuteScalarAsync());
    }
}
