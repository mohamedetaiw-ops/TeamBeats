using TeamBeats.Audit.Contracts;
using TeamBeats.Entitlements.Contracts;
using TeamBeats.Modules.Security.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Primitives;

namespace TeamBeats.Modules.Documents;

public sealed record LibraryEncryptionRequest(bool CustomerKey);

/// <summary>Whether a library's files use the customer's key, since when, how many stored versions still wait to move onto it, and whether it can be turned on.</summary>
public sealed record LibraryEncryptionView(Guid LibraryId, bool CustomerKey, DateTimeOffset? Since, int Pending, bool Available);

/// <summary>
/// Customer-managed keys per library (spec 011 R3, spec 026 FR-007). A library manager turns a TeamBeats library onto
/// the tenant data key when the plan has <c>security.customer_keys</c> and the tenant's key answers. From then on every
/// file, preview and controlled copy stored for it is encrypted with that key (<see cref="DocumentStorage"/>), and a
/// background pass moves the files stored before. It cannot be turned off: going back to platform keys is an operations
/// request, as for the tenant key itself.
/// </summary>
public sealed class LibraryEncryption(
    IDbSession session,
    IEntitlementService entitlements,
    Libraries libraries,
    ITenantDataKeys keys,
    DocumentStorage storage,
    IAuditWriter audit,
    IClock clock)
{
    private async Task<bool> AvailableAsync(CancellationToken ct)
    {
        if (!await entitlements.IsEnabledAsync(Capabilities.SecurityCustomerKeys, ct))
        {
            return false;
        }
        try
        {
            return await keys.CurrentAsync(ct) is not null;
        }
        catch (AppException e) when (e.Code == ProblemCodes.TenantKeyUnavailable)
        {
            return false;
        }
    }

    private async Task<LibraryEncryptionView> ViewAsync(Guid libraryId, CancellationToken ct)
    {
        var row = await session.QuerySingleOrDefaultAsync(
            """
            select l.customer_key, l.customer_key_at,
              (select count(*)::int from documents.document_version v where v.library_id = l.id and not v.encrypted and l.customer_key)
            from documents.library l where l.id = @libraryId
            """, new { libraryId }, r => (On: r.GetBoolean(0), Since: r.Get<DateTimeOffset?>("customer_key_at"), Pending: r.GetInt32(2)), ct);
        return new LibraryEncryptionView(libraryId, row.On, row.Since, row.Pending, row.On || await AvailableAsync(ct));
    }

    public async Task<LibraryEncryptionView> GetAsync(Guid libraryId, CancellationToken ct)
    {
        await libraries.ManagedAsync(libraryId, ct);
        return await ViewAsync(libraryId, ct);
    }

    public async Task<LibraryEncryptionView> SetAsync(Guid libraryId, LibraryEncryptionRequest request, CancellationToken ct)
    {
        var library = await libraries.ManagedAsync(libraryId, ct, forUpdate: true);
        library.RequireOwn();
        var current = await session.ScalarAsync<bool>("select customer_key from documents.library where id = @libraryId", new { libraryId }, ct);
        if (current == request.CustomerKey)
        {
            return await ViewAsync(libraryId, ct);
        }
        if (!request.CustomerKey)
        {
            throw AppException.Conflict("conflict.documents.customer_key_fixed");
        }
        await entitlements.DemandAsync(Capabilities.SecurityCustomerKeys, ct);
        if (!await AvailableAsync(ct))
        {
            throw AppException.Conflict("conflict.documents.customer_key_missing");
        }
        await session.ExecAsync("update documents.library set customer_key = true, customer_key_at = @now, row_version = row_version + 1 where id = @libraryId",
            new { libraryId, now = clock.UtcNow }, ct);
        await audit.WriteAsync(new AuditEntry(AuditActions.DocLibraryChanged, Securables.Library, libraryId.ToString(), TargetOrgUnitId: library.OrgUnitId,
            Changes: new { customerKey = new { before = false, after = true } }), ct);
        return await ViewAsync(libraryId, ct);
    }

    /// <summary>
    /// The background pass (document maintenance worker, as the system actor): moves up to <paramref name="limit"/>
    /// stored versions of libraries on the customer's key onto it, with their preview and controlled copy. A key that
    /// does not answer stops the pass until the next run; nothing is lost, as unmoved files still open.
    /// </summary>
    public async Task<int> RunAsync(int limit, CancellationToken ct)
    {
        var rows = await session.QueryAsync(
            """
            select v.id, v.object_key, v.preview_key, v.controlled_key from documents.document_version v
            join documents.library l on l.id = v.library_id
            where l.customer_key and not v.encrypted order by v.created_at, v.id limit @limit
            """, new { limit }, r => (Id: r.GetGuid(0), Key: r.GetString(1), Preview: r.Get<string>("preview_key"), Controlled: r.Get<string>("controlled_key")), ct);
        var done = 0;
        foreach (var (id, key, preview, controlled) in rows)
        {
            try
            {
                foreach (var file in new[] { key, preview, controlled }.OfType<string>())
                {
                    await storage.EncryptInPlaceAsync(file, ct);
                }
            }
            catch (AppException e) when (e.Code == ProblemCodes.TenantKeyUnavailable)
            {
                break;
            }
            await session.ExecAsync("update documents.document_version set encrypted = true where id = @id", new { id }, ct);
            done++;
        }
        return done;
    }
}
