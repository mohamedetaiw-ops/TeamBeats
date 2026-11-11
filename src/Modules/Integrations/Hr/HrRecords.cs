using System.Text.Json;
using TeamBeats.Entitlements.Contracts;
using TeamBeats.Modules.Integrations.Contracts;
using TeamBeats.Modules.Integrations.Runtime;
using TeamBeats.Persistence;
using TeamBeats.Primitives;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.Integrations.Hr;

/// <summary>
/// The <see cref="IHrRecords"/> contract: the stored copies from <see cref="HrSync"/>, and live reads of one record
/// through the REST pack for values that must be current (spec 019 US5 scenario 3). A live read never changes the
/// stored copy, so lifecycle planning only ever sees full syncs. The SFTP pack drops files and cannot read one record.
/// </summary>
public sealed class HrRecords(IDbSession session, ITenantContext context, IEntitlementService entitlements, IClock clock, HrSync sync, HrRestPack rest)
    : IHrRecords
{
    /// <summary>A person's matched HR record and, when the HR connection is the REST pack, that connection.</summary>
    internal sealed record Link(string Record, PackConnection? Connection);

    internal async Task<Link?> LinkAsync(Guid userId, CancellationToken ct)
    {
        var row = await session.QuerySingleOrDefaultAsync(
            """
            select e.record_id, c.id, c.kind, c.settings::text
            from integrations.hr_employee e
            join integrations.connection c on c.id = e.connection_id and c.tenant_id = e.tenant_id
            where e.user_id = @userId and e.match_state = 'matched' and c.enabled
            """, new { userId }, r => (Record: r.GetString(0), Id: r.GetGuid(1), Kind: r.GetString(2), Settings: r.GetString(3)), ct);
        if (row == default)
        {
            return null;
        }
        return new Link(row.Record, row.Kind == HrRestPack.KindName
            ? new PackConnection(context.RequireTenant(), row.Id, row.Kind, 1, null, JsonDocument.Parse(row.Settings).RootElement)
            : null);
    }

    public Task<IReadOnlyList<HrRecordView>> MatchedAsync(IReadOnlyCollection<Guid>? userIds = null, CancellationToken ct = default) =>
        sync.MatchedAsync(userIds, ct);

    public Task<IReadOnlyList<string>> MappedFieldsAsync(CancellationToken ct = default) => sync.MappedFieldsAsync(ct);

    public async Task<HrRecordRead> CurrentAsync(Guid userId, CancellationToken ct = default)
    {
        if (!await entitlements.IsEnabledAsync(Capabilities.HrIntegration, ct))
        {
            return new HrRecordRead(HrRecordRead.NotMatched, null);
        }
        var row = await LinkAsync(userId, ct);
        if (row is null)
        {
            return new HrRecordRead(HrRecordRead.NotMatched, null);
        }
        if (row.Connection is not { } connection)
        {
            return new HrRecordRead(HrRecordRead.Unavailable, null);
        }
        try
        {
            var (employee, _) = await rest.ReadOneAsync(connection, row.Record, ct);
            return employee is null
                ? new HrRecordRead(HrRecordRead.Unavailable, null)
                : new HrRecordRead(HrRecordRead.Ok, new HrRecordView(userId, row.Record, HrSync.Visible(HrSync.ToFields(employee)), clock.UtcNow));
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            return new HrRecordRead(HrRecordRead.Unavailable, null);
        }
    }

    public async Task<HrPreHire?> PreHireAsync(string recordId, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(recordId) || !await entitlements.IsEnabledAsync(Capabilities.HrIntegration, ct))
        {
            return null;
        }
        var row = await session.QuerySingleOrDefaultAsync(
            """
            select e.data->>'personal_email' as personal_email, e.data->>'hire_date' as hire_date, m.unit_id
            from integrations.hr_employee e
            left join integrations.hr_org_code m on m.tenant_id = e.tenant_id and m.code = e.data->>'org_unit_code'
            where e.record_id = @recordId and e.user_id is null and e.data->>'status' = 'active' and e.data ? 'personal_email'
            """, new { recordId }, r => (Email: r.GetString(0), Hire: r.Get<string?>("hire_date"), Unit: r.Get<Guid?>("unit_id")), ct);
        if (row == default)
        {
            return null;
        }
        DateOnly? hire = DateOnly.TryParseExact(row.Hire, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var d)
            ? d : null;
        return new HrPreHire(recordId, row.Email, hire, row.Unit);
    }
}
