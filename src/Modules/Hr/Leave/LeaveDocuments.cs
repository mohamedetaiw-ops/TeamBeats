using TeamBeats.Entitlements.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Primitives;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.Hr.Leave;

public sealed record LeaveDocumentView(Guid Id, string FileName, long SizeBytes, string ContentType, string ScanState);

/// <summary>
/// Supporting documents for leave (US1 scenario 3), such as a sick note: Restricted health data (section 8), stored
/// under the tenant's object path, scanned before use, readable by the person, their approver and HR officers covering
/// them, and never sent to AI.
/// </summary>
public sealed class LeaveDocuments(IDbSession session, ITenantContext context, TenantObjectStorage storage, IMalwareScanner scanner, IEntitlementService entitlements,
    IClock clock)
{
    public const int MaxBytes = 10 * 1024 * 1024;
    public const int MaxNameLength = 200;

    private static readonly IReadOnlySet<string> Allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "application/pdf", "image/png", "image/jpeg",
    };

    private static string ObjectKey(Guid id) => $"hr/leave/documents/{id:N}";

    public async Task<LeaveDocumentView> UploadAsync(string? fileName, string? contentType, byte[] data, CancellationToken ct)
    {
        await entitlements.DemandAsync(Capabilities.HrLeave, ct);
        var user = context.RequireUser();
        var file = Path.GetFileName((fileName ?? string.Empty).Replace('\\', '/'));
        var name = new string(file.Where(c => !char.IsControl(c) && c is not ('"' or '<' or '>' or '|' or ':' or '*' or '?')).ToArray()).Trim();
        if (name.Length == 0)
        {
            throw AppException.Validation("name", "validation.leave.document_name");
        }
        name = name.Length > MaxNameLength ? name[..MaxNameLength] : name;
        var type = (contentType ?? string.Empty).Split(';')[0].Trim().ToLowerInvariant();
        if (data.Length == 0 || !Allowed.Contains(type))
        {
            throw AppException.Validation("file", "validation.leave.document_type");
        }
        ScanVerdict verdict;
        using (var scan = new MemoryStream(data, writable: false))
        {
            verdict = await scanner.ScanAsync(scan, ct);
        }
        if (verdict == ScanVerdict.Infected)
        {
            throw AppException.Validation("file", "validation.leave.document_infected");
        }
        var id = Ids.New();
        using (var stream = new MemoryStream(data, writable: false))
        {
            await storage.PutAsync(ObjectKey(id), stream, type, ct);
        }
        var state = verdict == ScanVerdict.Clean ? "clean" : "failed";
        await session.ExecAsync(
            """
            insert into hr.leave_document (tenant_id, id, uploaded_by, object_key, file_name, size_bytes, content_type, scan_state, created_at)
            values (platform.current_tenant_id(), @id, @user, @key, @name, @size, @type, @state, @now)
            """, new { id, user, key = ObjectKey(id), name, size = data.LongLength, type, state, now = clock.UtcNow }, ct);
        return new LeaveDocumentView(id, name, data.LongLength, type, state);
    }

    /// <summary>Why the caller cannot attach the upload, or null: it must be their own, unattached and clean.</summary>
    internal async Task<string?> ProblemAsync(Guid id, CancellationToken ct)
    {
        var state = await session.ScalarAsync<string?>(
            "select scan_state from hr.leave_document where id = @id and uploaded_by = @user and request_id is null", new { id, user = context.RequireUser() }, ct);
        return state switch
        {
            null => "validation.leave.document_unknown",
            "clean" => null,
            _ => "validation.leave.document_not_clean",
        };
    }

    internal Task LinkAsync(Guid id, Guid requestId, CancellationToken ct) =>
        session.ExecAsync("update hr.leave_document set request_id = @requestId where id = @id and request_id is null", new { id, requestId }, ct);

    /// <summary>Opens the document of a request the caller may read (checked by the caller).</summary>
    internal async Task<(Stream Content, string ContentType, string FileName)> OpenAsync(Guid requestId, CancellationToken ct)
    {
        var row = await session.QuerySingleOrDefaultAsync(
            "select id, file_name, content_type, scan_state from hr.leave_document where request_id = @requestId",
            new { requestId }, r => (Id: r.GetGuid(0), Name: r.GetString(1), Type: r.GetString(2), State: r.GetString(3)), ct);
        if (row.Name is null)
        {
            throw AppException.NotFound();
        }
        if (row.State != "clean")
        {
            throw AppException.Conflict("conflict.leave.document_not_clean");
        }
        var content = await storage.OpenReadAsync(ObjectKey(row.Id), ct) ?? throw AppException.NotFound();
        return (content, row.Type, row.Name);
    }
}
