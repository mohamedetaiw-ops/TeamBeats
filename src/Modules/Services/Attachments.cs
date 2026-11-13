using TeamBeats.Persistence;
using TeamBeats.Primitives;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.Services;

/// <summary>
/// Request attachments (US2 scenario 4): stored under the tenant's object path, scanned before anything else can use
/// them, and readable only by people who may read the request, once clean. They share the request's classification.
/// </summary>
public sealed class Attachments(IDbSession session, ITenantContext context, TenantObjectStorage storage, IMalwareScanner scanner, IClock clock)
{
    public const int MaxBytes = 10 * 1024 * 1024;
    public const int MaxNameLength = 200;

    private static readonly IReadOnlySet<string> InlineTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "application/pdf", "image/png", "image/jpeg", "text/plain",
    };

    private static string ObjectKey(Guid id) => $"services/attachments/{id:N}";

    private static string CleanName(string? name)
    {
        var file = Path.GetFileName((name ?? string.Empty).Replace('\\', '/'));
        var safe = new string(file.Where(c => !char.IsControl(c) && c is not ('"' or '<' or '>' or '|' or ':' or '*' or '?')).ToArray()).Trim();
        if (safe.Length == 0)
        {
            throw AppException.Validation("name", "validation.services.attachment_name");
        }
        return safe.Length > MaxNameLength ? safe[..MaxNameLength] : safe;
    }

    /// <summary>Stores and scans an upload. Infected files are deleted at once and refused.</summary>
    public async Task<AttachmentView> UploadAsync(string? fileName, string? contentType, byte[] data, CancellationToken ct)
    {
        var user = context.RequireUser();
        var name = CleanName(fileName);
        if (data.Length == 0)
        {
            throw AppException.Validation("file", "validation.services.attachment_empty");
        }
        var type = string.IsNullOrWhiteSpace(contentType) || contentType.Length > 100 ? "application/octet-stream" : contentType.Split(';')[0].Trim().ToLowerInvariant();
        ScanVerdict verdict;
        using (var scan = new MemoryStream(data, writable: false))
        {
            verdict = await scanner.ScanAsync(scan, ct);
        }
        if (verdict == ScanVerdict.Infected)
        {
            throw AppException.Validation("file", "validation.services.attachment_infected");
        }
        var id = Ids.New();
        using (var stream = new MemoryStream(data, writable: false))
        {
            await storage.PutAsync(ObjectKey(id), stream, type, ct);
        }
        var state = verdict == ScanVerdict.Clean ? "clean" : "failed";
        await session.ExecAsync(
            """
            insert into services.attachment (tenant_id, id, uploaded_by, object_key, file_name, size_bytes, content_type, scan_state, created_at)
            values (platform.current_tenant_id(), @id, @user, @key, @name, @size, @type, @state, @now)
            """, new { id, user, key = ObjectKey(id), name, size = data.LongLength, type, state, now = clock.UtcNow }, ct);
        return new AttachmentView(id, name, data.LongLength, type, state);
    }

    /// <summary>Why the caller cannot attach these uploads, or null: each must be their own, unattached and clean.</summary>
    internal async Task<string?> ProblemAsync(IReadOnlySet<Guid> ids, CancellationToken ct)
    {
        var user = context.RequirePrincipal();
        var rows = await session.QueryAsync("select id, scan_state from services.attachment where id = any(@ids) and uploaded_by = @user and request_id is null",
            new { ids = ids.ToArray(), user }, r => r.GetString(1), ct);
        if (rows.Count != ids.Count)
        {
            return "validation.services.attachment_unknown";
        }
        return rows.All(s => s == "clean") ? null : "validation.services.attachment_not_clean";
    }

    internal Task LinkAsync(Guid requestId, IReadOnlySet<Guid> ids, CancellationToken ct) =>
        ids.Count == 0 ? Task.CompletedTask
            : session.ExecAsync("update services.attachment set request_id = @requestId where id = any(@ids) and request_id is null", new { requestId, ids = ids.ToArray() }, ct);

    /// <summary>Which of the given attachments already belong to the request.</summary>
    internal async Task<IReadOnlySet<Guid>> LinkedAsync(Guid requestId, IReadOnlySet<Guid> ids, CancellationToken ct) =>
        ids.Count == 0 ? new HashSet<Guid>()
            : (await session.QueryAsync("select id from services.attachment where request_id = @requestId and id = any(@ids)", new { requestId, ids = ids.ToArray() },
                r => r.GetGuid(0), ct)).ToHashSet();

    /// <summary>Lets go of the request's attachments that are not kept; they become unattached uploads of the person who uploaded them.</summary>
    internal Task ReleaseAsync(Guid requestId, IReadOnlySet<Guid> keep, CancellationToken ct) =>
        session.ExecAsync("update services.attachment set request_id = null where request_id = @requestId and not (id = any(@keep))",
            new { requestId, keep = keep.ToArray() }, ct);

    internal async Task<IReadOnlyList<AttachmentView>> ForRequestAsync(Guid requestId, CancellationToken ct) =>
        await session.QueryAsync("select id, file_name, size_bytes, content_type, scan_state from services.attachment where request_id = @requestId order by created_at, id",
            new { requestId }, r => new AttachmentView(r.GetGuid(0), r.GetString(1), r.GetInt64(2), r.GetString(3), r.GetString(4)), ct);

    /// <summary>Opens an attachment of a request the caller may read (checked by the caller). Not clean means not downloadable.</summary>
    internal async Task<(Stream Content, string ContentType, string FileName)> OpenAsync(Guid requestId, Guid attachmentId, CancellationToken ct)
    {
        var row = await session.QuerySingleOrDefaultAsync(
            "select file_name, content_type, scan_state from services.attachment where id = @attachmentId and request_id = @requestId",
            new { attachmentId, requestId }, r => (Name: r.GetString(0), Type: r.GetString(1), State: r.GetString(2)), ct);
        if (row.Name is null)
        {
            throw AppException.NotFound();
        }
        if (row.State != "clean")
        {
            throw AppException.Conflict("conflict.services.attachment_not_clean");
        }
        var content = await storage.OpenReadAsync(ObjectKey(attachmentId), ct) ?? throw AppException.NotFound();
        return (content, InlineTypes.Contains(row.Type) ? row.Type : "application/octet-stream", row.Name);
    }
}
