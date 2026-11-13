using TeamBeats.Audit.Contracts;
using TeamBeats.Authorization.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Primitives;

namespace TeamBeats.Modules.Audit;

/// <summary><see cref="IAuditExports"/> over the export jobs of the current tenant (forced RLS keeps it to that tenant).</summary>
public sealed class AuditExports(IDbSession session, IAuthorizationService authz, TenantObjectStorage storage, IClock clock) : IAuditExports
{
    private const string Columns = "id, format, row_count, completed_at, expires_at";

    private static AuditExportSummary Read(Npgsql.NpgsqlDataReader r) =>
        new(r.GetGuid(0), r.GetString(1), r.IsDBNull(2) ? null : r.GetInt64(2), r.GetFieldValue<DateTimeOffset>(3), r.GetFieldValue<DateTimeOffset>(4));

    public async Task<IReadOnlyList<AuditExportSummary>> ListAvailableAsync(int limit, CancellationToken ct = default)
    {
        await authz.DemandAsync(Permissions.AuditExport, AuthzTarget.Tenant, ct);
        return await session.QueryAsync(
            $"select {Columns} from audit.export_job where status = 'completed' and expires_at > @now order by completed_at desc limit @limit",
            new { now = clock.UtcNow, limit = Math.Clamp(limit, 1, 100) }, Read, ct);
    }

    public async Task<(AuditExportSummary Export, Stream Content)?> OpenAsync(Guid exportId, CancellationToken ct = default)
    {
        await authz.DemandAsync(Permissions.AuditExport, AuthzTarget.Tenant, ct);
        var export = await session.QuerySingleOrDefaultAsync(
            $"select {Columns} from audit.export_job where id = @exportId and status = 'completed' and expires_at > @now",
            new { exportId, now = clock.UtcNow }, Read, ct);
        if (export is null || await storage.OpenReadAsync(AuditExportHandler.RelativeKey(export.Id, export.Format), ct) is not { } content)
        {
            return null;
        }
        return (export, content);
    }
}
