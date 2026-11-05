using TeamBeats.Persistence;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.Tenancy;

/// <summary>Reads the platform tenant registry. This is the only path other code has to <c>tenancy.tenant</c>.</summary>
public sealed class TenantRegistry(IDbSession session) : ITenantRegistry, ITenantLister
{
    private const string Columns = "select id, key, status from tenancy.tenant";

    private static TenantInfo Map(Npgsql.NpgsqlDataReader r) =>
        new(r.GetGuid(0), r.GetString(1), Sql.ParseEnum<TenantLifecycleStatus>(r.GetString(2)));

    public Task<TenantInfo?> FindByKeyAsync(string key, CancellationToken ct) =>
        session.QuerySingleOrDefaultAsync(Columns + " where key = @key", new { key = TenantRules.NormalizeKey(key) }, Map, ct);

    public Task<TenantInfo?> FindByIdAsync(Guid id, CancellationToken ct) =>
        session.QuerySingleOrDefaultAsync(Columns + " where id = @id", new { id }, Map, ct);

    public async Task<IReadOnlyList<TenantInfo>> ListAsync(CancellationToken ct) =>
        await session.QueryAsync(Columns + " order by key", null, Map, ct);
}
