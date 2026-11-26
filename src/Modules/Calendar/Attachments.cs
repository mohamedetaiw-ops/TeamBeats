using TeamBeats.Audit.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Primitives;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.Calendar;

/// <summary>
/// Files attached to an event (spec 037 US10, FR-033): the agenda, slides or a map. Up to 5 per event of at most 20 MB,
/// recognised by their signature, scanned for malware and kept in tenant object storage. They are read only through the
/// calendar API by the event's audience and its managers, and go with the event.
/// </summary>
public sealed partial class CompanyEvents
{
    public const int MaxAttachments = 5;
    public const long MaxAttachmentBytes = 20L * 1024 * 1024;
    public const int MaxAttachmentName = 200;

    private const string Pdf = "application/pdf";
    private const string Docx = "application/vnd.openxmlformats-officedocument.wordprocessingml.document";
    private const string Xlsx = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";
    private const string Pptx = "application/vnd.openxmlformats-officedocument.presentationml.presentation";

    public async Task<AttachmentView> AttachAsync(Guid id, string? fileName, string? contentType, byte[] data, CancellationToken ct)
    {
        var row = await ManagedAsync(id, ct);
        var name = Path.GetFileName((fileName ?? "").Trim());
        if (name.Length is 0 or > MaxAttachmentName)
        {
            throw AppException.Validation("file", "validation.calendar_event.attachment_name");
        }
        if (data.Length == 0 || data.Length > MaxAttachmentBytes)
        {
            throw AppException.Validation("file", "validation.calendar_event.attachment_size");
        }
        if (TypeOf(name, data) is not { } type)
        {
            throw AppException.Validation("file", "validation.calendar_event.attachment_type");
        }
        _ = contentType;
        var count = await session.ScalarAsync<long>("select count(*) from calendar.attachment where event_id = @id", new { id }, ct);
        if (count >= MaxAttachments)
        {
            throw AppException.Conflict("conflict.calendar_event.attachment_limit");
        }
        ScanVerdict verdict;
        using (var scan = new MemoryStream(data, writable: false))
        {
            verdict = await scanner.ScanAsync(scan, ct);
        }
        if (verdict != ScanVerdict.Clean)
        {
            throw AppException.Validation("file", verdict == ScanVerdict.Infected ? "validation.calendar_event.attachment_infected" : "validation.calendar_event.attachment_scan_failed");
        }
        var attachmentId = Ids.New();
        using (var stream = new MemoryStream(data, writable: false))
        {
            await storage.PutAsync(AttachmentKey(id, attachmentId), stream, type, ct);
        }
        var now = clock.UtcNow;
        await session.ExecAsync(
            """
            insert into calendar.attachment (tenant_id, id, event_id, name, content_type, size, created_by, created_at)
            values (platform.current_tenant_id(), @attachmentId, @id, @name, @type, @size, @me, @now)
            """, new { attachmentId, id, name, type, size = (long)data.Length, me = context.RequireUser(), now }, ct);
        await audit.WriteAsync(new AuditEntry(AuditActions.CalendarAttachmentAdded, "calendar_event", id.ToString(), TargetOrgUnitId: row.OrgUnitId,
            Changes: new { attachment = attachmentId, name, size = data.Length, type }), ct);
        return new AttachmentView(attachmentId, name, type, data.Length, now);
    }

    public async Task RemoveAttachmentAsync(Guid id, Guid attachmentId, CancellationToken ct)
    {
        var row = await ManagedAsync(id, ct);
        var name = await session.QuerySingleOrDefaultAsync("delete from calendar.attachment where id = @attachmentId and event_id = @id returning name",
            new { attachmentId, id }, r => r.GetString(0), ct) ?? throw AppException.NotFound();
        await storage.DeleteAsync(AttachmentKey(id, attachmentId), ct);
        await audit.WriteAsync(new AuditEntry(AuditActions.CalendarAttachmentRemoved, "calendar_event", id.ToString(), TargetOrgUnitId: row.OrgUnitId,
            Changes: new { attachment = attachmentId, name }), ct);
    }

    /// <summary>Opens an attachment for someone in the event's audience or one of its managers; anyone else gets not found.</summary>
    public async Task<(Stream Content, AttachmentView File)> OpenAttachmentAsync(Guid id, Guid attachmentId, CancellationToken ct)
    {
        var me = context.RequireUser();
        var row = await RowAsync(id, ct) ?? throw AppException.NotFound();
        if (!await audiences.IncludesAsync(row.AppliesTo, me, ct))
        {
            var coverage = await authz.CoverageAsync(TeamBeats.Authorization.Contracts.Permissions.ContentPublish, ct);
            if (!Manages(coverage, row))
            {
                throw AppException.NotFound();
            }
        }
        var file = (await AttachmentsOfAsync(id, ct)).FirstOrDefault(a => a.Id == attachmentId) ?? throw AppException.NotFound();
        var content = await storage.OpenReadAsync(AttachmentKey(id, attachmentId), ct) ?? throw AppException.NotFound();
        return (content, file);
    }

    private async Task<IReadOnlyList<AttachmentView>> AttachmentsOfAsync(Guid id, CancellationToken ct) =>
        (await AttachmentsOfAsync([id], ct)).GetValueOrDefault(id) ?? [];

    private async Task<Dictionary<Guid, IReadOnlyList<AttachmentView>>> AttachmentsOfAsync(Guid[] ids, CancellationToken ct)
    {
        if (ids.Length == 0)
        {
            return [];
        }
        var rows = await session.QueryAsync(
            "select event_id, id, name, content_type, size, created_at from calendar.attachment where event_id = any(@ids) order by created_at, id",
            new { ids }, r => (Event: r.GetGuid(0), View: new AttachmentView(r.GetGuid(1), r.GetString(2), r.GetString(3), r.GetInt64(4), r.GetFieldValue<DateTimeOffset>(5))), ct);
        return rows.GroupBy(r => r.Event).ToDictionary(g => g.Key, g => (IReadOnlyList<AttachmentView>)g.Select(r => r.View).ToList());
    }

    /// <summary>
    /// The type from the file's own bytes: PDF, PNG and JPEG by their signature; Word, Excel and PowerPoint as a zip
    /// package whose extension says which. Anything else is refused, whatever the browser claimed.
    /// </summary>
    internal static string? TypeOf(string name, byte[] data)
    {
        static bool Starts(byte[] d, params byte[] sig) => d.Length >= sig.Length && sig.Select((b, i) => d[i] == b).All(x => x);
        var extension = Path.GetExtension(name).ToLowerInvariant();
        if (Starts(data, 0x25, 0x50, 0x44, 0x46, 0x2D))
        {
            return extension == ".pdf" ? Pdf : null;
        }
        if (Starts(data, 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A))
        {
            return extension == ".png" ? "image/png" : null;
        }
        if (Starts(data, 0xFF, 0xD8, 0xFF))
        {
            return extension is ".jpg" or ".jpeg" ? "image/jpeg" : null;
        }
        if (Starts(data, 0x50, 0x4B, 0x03, 0x04))
        {
            return extension switch
            {
                ".docx" => Docx,
                ".xlsx" => Xlsx,
                ".pptx" => Pptx,
                _ => null,
            };
        }
        return null;
    }
}
