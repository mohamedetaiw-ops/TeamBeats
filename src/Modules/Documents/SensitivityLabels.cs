using TeamBeats.Audit.Contracts;
using TeamBeats.Authorization.Contracts;
using TeamBeats.Entitlements.Contracts;
using TeamBeats.Messaging;
using TeamBeats.Modules.Integrations.Contracts;
using TeamBeats.Modules.Search.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Primitives;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.Documents;

/// <summary>
/// A Microsoft Purview label seen on files of a connection's connected libraries, or mapped before any file carried it,
/// with the TeamBeats classification it maps to (null when unmapped, which counts as confidential).
/// </summary>
public sealed record SensitivityLabelView(string LabelId, string? Name, long Files, string? Classification);

public sealed record SensitivityLabelMapRequest(string? Classification);

/// <summary>Asks for the files of one label on one connection to be indexed again, after its mapping changed.</summary>
public sealed record SensitivityLabelRemapped(Guid ConnectionId, string LabelId) : IIntegrationEvent;

/// <summary>
/// The mapping of Microsoft Purview sensitivity labels to the four TeamBeats classifications (spec 011 FR-032), per
/// Microsoft 365 connection because labels belong to its directory. Unlabelled files take their library's default
/// classification, and a label with no mapping counts as confidential, so a new label never widens who finds a file.
/// The integration administrators who manage the connection keep the mapping. Labels are listed as the sync sees them
/// on files, so no extra Microsoft permission is needed.
/// </summary>
public sealed class SensitivityLabels(IDbSession session, ITenantContext context, IAuthorizationService authz, IEntitlementService entitlements,
    IAuditWriter audit, IOutbox outbox, IClock clock, IConnectionFeatures connections) : IMessageHandler<SensitivityLabelRemapped>
{
    public const int MaxLabels = 500;

    public async Task<IReadOnlyList<SensitivityLabelView>> ListAsync(Guid connectionId, CancellationToken ct)
    {
        await DemandAsync(connectionId, ct);
        return await session.QueryAsync(
            """
            with seen as (
              select i.label_id, max(i.label_name) as name, count(*) as files
              from documents.connected_item i join documents.library l on l.id = i.library_id
              where l.connection_id = @connectionId and i.label_id is not null and not i.folder
              group by i.label_id
            )
            select coalesce(s.label_id, m.label_id), s.name, coalesce(s.files, 0), m.classification
            from seen s full join (select label_id, classification from documents.sensitivity_label_map where connection_id = @connectionId) m on m.label_id = s.label_id
            order by s.name nulls last, 1 limit @max
            """, new { connectionId, max = MaxLabels },
            r => new SensitivityLabelView(r.GetString(0), r.Get<string>("name"), r.GetInt64(2), r.Get<string>("classification")), ct);
    }

    /// <summary>Maps a label to a classification, or removes its mapping when <paramref name="request"/> names none.</summary>
    public async Task<SensitivityLabelView> MapAsync(Guid connectionId, string labelId, SensitivityLabelMapRequest request, CancellationToken ct)
    {
        await DemandAsync(connectionId, ct);
        if (!Guid.TryParse(labelId, out var label))
        {
            throw AppException.Validation("labelId", "validation.documents.sensitivity_label");
        }
        var id = label.ToString("D");
        if (request.Classification is { } classification)
        {
            if (!ConditionAttributes.Classifications.Contains(classification))
            {
                throw AppException.Validation("classification", "validation.documents.classification");
            }
            await session.ExecAsync(
                """
                insert into documents.sensitivity_label_map (tenant_id, connection_id, label_id, classification, updated_by, updated_at)
                values (platform.current_tenant_id(), @connectionId, @id, @classification, @by, @now)
                on conflict (tenant_id, connection_id, label_id) do update set classification = @classification, updated_by = @by, updated_at = @now
                """, new { connectionId, id, classification, by = context.RequireUser(), now = clock.UtcNow }, ct);
        }
        else
        {
            await session.ExecAsync("delete from documents.sensitivity_label_map where connection_id = @connectionId and label_id = @id", new { connectionId, id }, ct);
        }
        await audit.WriteAsync(new AuditEntry(AuditActions.DocLabelMapped, "connection", connectionId.ToString(),
            Changes: new { label = id, classification = request.Classification }), ct);
        await outbox.EnqueueAsync(new SensitivityLabelRemapped(connectionId, id), ct);
        return (await ListAsync(connectionId, ct)).FirstOrDefault(l => l.LabelId == id) ?? new SensitivityLabelView(id, null, 0, null);
    }

    public async Task HandleAsync(SensitivityLabelRemapped message, CancellationToken ct)
    {
        var files = await session.QueryAsync(
            """
            select i.id from documents.connected_item i join documents.library l on l.id = i.library_id
            where l.connection_id = @connection and i.label_id = @label and not i.folder
            """, new { connection = message.ConnectionId, label = message.LabelId }, r => r.GetGuid(0), ct);
        foreach (var file in files)
        {
            await outbox.EnqueueAsync(new SearchItemChanged(ConnectedSync.SearchSource, file), ct);
        }
    }

    /// <summary>The integration administrator of this tenant, and a connection of this tenant with SharePoint documents turned on.</summary>
    private async Task DemandAsync(Guid connectionId, CancellationToken ct)
    {
        await entitlements.DemandAsync(Capabilities.DocsCore, ct);
        context.RequireUser();
        await authz.DemandAsync(Permissions.IntegrationManage, AuthzTarget.Tenant, ct);
        if ((await connections.FindAsync(ConnectionKinds.Microsoft365, Microsoft365Features.SharePointDocuments, ct))?.ConnectionId != connectionId)
        {
            throw AppException.NotFound();
        }
    }
}
