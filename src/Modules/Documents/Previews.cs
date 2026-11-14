using TeamBeats.Audit.Contracts;
using TeamBeats.Localization;
using TeamBeats.Messaging;
using TeamBeats.Modules.Documents.Contracts;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Primitives;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.Documents;

/// <summary>How a version shows in the browser: <c>pdf</c>, <c>image</c>, <c>video</c>, <c>text</c> or <c>unavailable</c>.</summary>
/// <remarks>
/// <c>Watermark</c> is set for a reader of a view-only library (spec 011 FR-016): the page lays it over the preview and
/// offers no download, print or copy; a PDF preview also carries it on every page.
/// </remarks>
public sealed record PreviewView(string Kind, string? Url, string? ContentType, DateTimeOffset? ExpiresAt, string? Reason, string? Watermark = null);

public static class PreviewKinds
{
    public const string Pdf = "pdf";
    public const string Image = "image";
    public const string Video = "video";
    public const string Text = "text";
    public const string Unavailable = "unavailable";

    /// <summary>Why there is no preview, as a code the page turns into a message with a download where allowed.</summary>
    public const string ReasonFormat = "format";
    public const string ReasonNoConverter = "not_configured";
    public const string ReasonFailed = "failed";

    private static readonly IReadOnlySet<string> Office = new HashSet<string>(StringComparer.Ordinal)
    {
        "doc", "docx", "xls", "xlsx", "ppt", "pptx", "odt", "ods", "odp", "rtf",
    };

    /// <summary>The kind for a stored content type and file name; Office formats need conversion first.</summary>
    public static (string Kind, bool Convert) Of(string contentType, string fileName)
    {
        if (contentType == "application/pdf")
        {
            return (Pdf, false);
        }
        if (contentType.StartsWith("image/", StringComparison.Ordinal))
        {
            return (Image, false);
        }
        if (contentType.StartsWith("video/", StringComparison.Ordinal))
        {
            return (Video, false);
        }
        if (contentType is "text/plain" or "text/csv" or "text/markdown")
        {
            return (Text, false);
        }
        return Office.Contains(FileTypes.ExtensionOf(fileName)) ? (Pdf, true) : (Unavailable, false);
    }
}

/// <summary>
/// Previews (spec 011 FR-009, plan "Preview"). PDF, images, video and text open from the file itself; Office files are
/// converted to PDF by the <see cref="IDocumentConverter"/> on first request and kept per version. Only clean versions the
/// caller may open get a preview; the preview is served through its own short-lived token, inline and sandboxed.
/// </summary>
public sealed class DocumentPreviews(
    IDbSession session,
    ITenantContext context,
    DocumentFiles files,
    Libraries libraries,
    TenantObjectStorage storage,
    IDocumentConverter converter,
    ILocalizer localizer,
    ITenantSettingsReader tenantSettings,
    IAuditWriter audit,
    IOutbox outbox,
    IClock clock)
{
    public const string FooterKey = "documents.controlled_footer";

    private sealed record Row(Guid Library, string Key, string ContentType, string FileName, string State, string? PreviewKey);

    public async Task<PreviewView> PreviewAsync(Guid documentId, Guid? versionId, CancellationToken ct)
    {
        var (target, watermark) = await files.PreviewableAsync(documentId, versionId, ct);
        var row = await RowAsync(target, forUpdate: false, ct);
        var (kind, convert) = PreviewKinds.Of(row.ContentType, row.FileName);
        if (kind == PreviewKinds.Unavailable)
        {
            return new PreviewView(kind, null, null, null, PreviewKinds.ReasonFormat);
        }
        if (convert && row.State != "ready")
        {
            if (row.State == "failed")
            {
                return new PreviewView(PreviewKinds.Unavailable, null, null, null, PreviewKinds.ReasonFailed);
            }
            if (!converter.Available)
            {
                return new PreviewView(PreviewKinds.Unavailable, null, null, null, PreviewKinds.ReasonNoConverter);
            }
            if (!await ConvertAsync(documentId, target, row, ct))
            {
                return new PreviewView(PreviewKinds.Unavailable, null, null, null, PreviewKinds.ReasonFailed);
            }
        }
        var classification = await session.ScalarAsync<string>("select classification from documents.document where id = @documentId", new { documentId }, ct);
        await files.NoteGuestOpenAsync(documentId, row.Library, classification ?? "", ct);
        if (await libraries.Access.LibraryAsync(row.Library, ct) is { } library)
        {
            await files.NoteGuestViewAsync(library, documentId, ct);
        }
        var (token, expires) = await files.IssueTokenAsync(documentId, target, TokenPurposes.Preview, ct, watermark);
        return new PreviewView(kind, $"/api/v1/documents/previews/{token}", convert ? "application/pdf" : row.ContentType, expires, null, watermark);
    }

    private async Task<Row> RowAsync(Guid versionId, bool forUpdate, CancellationToken ct) =>
        (await session.QuerySingleOrDefaultAsync(
            "select library_id, object_key, content_type, file_name, preview_state, preview_key from documents.document_version where id = @versionId"
            + (forUpdate ? " for update" : ""),
            new { versionId }, r => new Row(r.GetGuid(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4), r.Get<string>("preview_key")), ct))
        ?? throw AppException.NotFound();

    /// <summary>Converts once per version; a second request waiting on the row lock finds the PDF already there.</summary>
    private async Task<bool> ConvertAsync(Guid documentId, Guid versionId, Row row, CancellationToken ct)
    {
        var locked = await RowAsync(versionId, forUpdate: true, ct);
        if (locked.State != "none")
        {
            return locked.State == "ready";
        }
        await using var original = await storage.OpenReadAsync(row.Key, ct) ?? throw AppException.NotFound();
        await using var pdf = await converter.ToPdfAsync(original, row.FileName, ct);
        if (pdf is null)
        {
            await session.ExecAsync("update documents.document_version set preview_state = 'failed' where id = @versionId", new { versionId }, ct);
            return false;
        }
        var key = DocumentFiles.ObjectKey(row.Library, documentId, versionId) + ".preview.pdf";
        await storage.PutAsync(key, pdf, "application/pdf", ct);
        await session.ExecAsync("update documents.document_version set preview_state = 'ready', preview_key = @key where id = @versionId", new { versionId, key }, ct);
        return true;
    }

    /// <summary>
    /// Opens a preview token: the PDF made for an Office file, or the file itself for formats the browser shows. Only for
    /// the person it was issued to, in its tenant, until it expires; never the original of a file that needs conversion.
    /// </summary>
    public async Task<(Stream Content, string ContentType)> OpenAsync(string token, CancellationToken ct)
    {
        var user = context.RequireUser();
        if (string.IsNullOrEmpty(token) || token.Length > 100)
        {
            throw AppException.NotFound();
        }
        var row = await session.QuerySingleOrDefaultAsync(
            """
            select v.object_key, v.content_type, v.file_name, v.scan_state, v.preview_state, v.preview_key, t.watermark, v.size_bytes
            from documents.download_token t
            join documents.document_version v on v.id = t.version_id
            join documents.document d on d.id = t.document_id
            where t.token_hash = @hash and t.user_id = @user and t.expires_at > @now and t.purpose = 'preview' and d.deleted_at is null
            """, new { hash = DocumentFiles.TokenHash(token), user, now = clock.UtcNow },
            r => (Key: r.GetString(0), Type: r.GetString(1), Name: r.GetString(2), Scan: r.GetString(3), State: r.GetString(4), Preview: r.Get<string>("preview_key"),
                Watermark: r.Get<string>("watermark"), Size: r.GetInt64(7)), ct);
        if (row.Key is null || row.Scan != ScanStates.Clean)
        {
            throw AppException.NotFound();
        }
        var (kind, convert) = PreviewKinds.Of(row.Type, row.Name);
        var (key, type) = convert ? (row.State == "ready" ? row.Preview : null, "application/pdf") : (kind == PreviewKinds.Unavailable ? null : row.Key, row.Type);
        if (key is null)
        {
            throw AppException.NotFound();
        }
        var content = await storage.OpenReadAsync(key, ct) ?? throw AppException.NotFound();
        if (row.Watermark is { } text && type == "application/pdf")
        {
            // A view-only preview carries the viewer's watermark on every page (spec 011 FR-016).
            content = await files.StampTextAsync(content, convert ? (content.CanSeek ? content.Length : 0) : row.Size, text, ct);
        }
        return (content, type);
    }

    // ---- Controlled copy (US3 scenario 6) ----

    /// <summary>
    /// A PDF of a released version of a controlled document with its control footer on every page: number, version,
    /// effective date and "uncontrolled when printed". Made once per version and kept; served through its own token.
    /// </summary>
    public async Task<DownloadTokenView> ControlledCopyAsync(Guid documentId, Guid? versionId, CancellationToken ct)
    {
        var target = await files.DownloadableAsync(documentId, versionId, ct);
        var doc = (await files.RowAsync(documentId, false, ct))!;
        if (doc.Number is not { } number || await libraries.ControlledOfAsync(doc.TypeId, ct) is null)
        {
            throw AppException.Conflict("conflict.documents.not_controlled");
        }
        var version = (await files.VersionAsync(target, ct))!;
        var effectiveFrom = await session.QuerySingleOrDefaultAsync("select effective_from from documents.document_version where id = @target", new { target },
            r => r.Get<DateTimeOffset?>("effective_from"), ct);
        if (effectiveFrom is not { } from || version.State is not (VersionStates.Approved or VersionStates.Effective or VersionStates.Superseded))
        {
            throw AppException.Conflict("conflict.documents.not_released");
        }
        if (await session.ScalarAsync<string>("select controlled_key from documents.document_version where id = @target", new { target }, ct) is null)
        {
            await StampAsync(documentId, target, number, version.Label, from, ct);
        }
        var (token, expires) = await files.IssueTokenAsync(documentId, target, TokenPurposes.ControlledCopy, ct);
        return new DownloadTokenView(token, $"/api/v1/documents/controlled/{token}", expires);
    }

    private async Task StampAsync(Guid documentId, Guid versionId, string number, string label, DateTimeOffset from, CancellationToken ct)
    {
        var row = await RowAsync(versionId, forUpdate: true, ct);
        var (kind, convert) = PreviewKinds.Of(row.ContentType, row.FileName);
        if (kind != PreviewKinds.Pdf)
        {
            throw AppException.Conflict("conflict.documents.no_pdf");
        }
        string source;
        if (convert)
        {
            if (row.State != "ready" && (row.State == "failed" || !converter.Available || !await ConvertAsync(documentId, versionId, row, ct)))
            {
                throw AppException.Conflict("conflict.documents.copy_unavailable");
            }
            source = (await RowAsync(versionId, forUpdate: false, ct)).PreviewKey!;
        }
        else
        {
            source = row.Key;
        }
        var zone = TimeZoneInfo.FindSystemTimeZoneById((await tenantSettings.GetLocaleSettingsAsync(ct)).TimeZone);
        var date = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(from.UtcDateTime, zone)).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        // Stamped in English: the conversion service draws standard PDF fonts, which do not join Arabic letters.
        var text = localizer.Get(FooterKey, "en").Replace("{number}", number, StringComparison.Ordinal).Replace("{version}", label, StringComparison.Ordinal)
            .Replace("{date}", date, StringComparison.Ordinal);
        await using var original = await storage.OpenReadAsync(source, ct) ?? throw AppException.NotFound();
        await using var stamped = await converter.StampFooterAsync(original, text, ct) ?? throw AppException.Conflict("conflict.documents.copy_unavailable");
        var key = DocumentFiles.ObjectKey(row.Library, documentId, versionId) + ".controlled.pdf";
        await storage.PutAsync(key, stamped, "application/pdf", ct);
        await session.ExecAsync("update documents.document_version set controlled_key = @key where id = @versionId", new { versionId, key }, ct);
    }

    /// <summary>Opens a controlled copy token: a download, so it is counted and audited like one.</summary>
    public async Task<(Stream Content, string FileName)> OpenControlledAsync(string token, CancellationToken ct)
    {
        var user = context.RequireUser();
        if (string.IsNullOrEmpty(token) || token.Length > 100)
        {
            throw AppException.NotFound();
        }
        var row = await session.QuerySingleOrDefaultAsync(
            """
            select t.document_id, t.version_id, v.controlled_key, v.major, v.minor, d.number, d.classification, d.library_id
            from documents.download_token t
            join documents.document_version v on v.id = t.version_id
            join documents.document d on d.id = t.document_id
            where t.token_hash = @hash and t.user_id = @user and t.expires_at > @now and t.purpose = 'controlled_copy' and d.deleted_at is null
            """, new { hash = DocumentFiles.TokenHash(token), user, now = clock.UtcNow },
            r => (Doc: r.GetGuid(0), Version: r.GetGuid(1), Key: r.Get<string>("controlled_key"), Label: VersionLabels.Of(r.GetInt32(3), r.GetInt32(4)),
                Number: r.Get<string>("number"), Classification: r.GetString(6), Library: r.GetGuid(7)), ct);
        if (row.Key is null)
        {
            throw AppException.NotFound();
        }
        var content = await storage.OpenReadAsync(row.Key, ct) ?? throw AppException.NotFound();
        if (row.Classification is "confidential" or "restricted")
        {
            await audit.WriteAsync(new AuditEntry(AuditActions.DocDownloaded, Securables.Document, row.Doc.ToString(),
                Changes: new { version = row.Version, library = row.Library, controlledCopy = true }), ct);
        }
        await outbox.EnqueueAsync(new DocumentDownloaded(row.Doc, row.Library, user, content.CanSeek ? content.Length : 0), ct);
        return (content, $"{row.Number} {row.Label}.pdf");
    }
}
