using TeamBeats.Entitlements.Contracts;
using TeamBeats.Messaging;
using TeamBeats.Modules.Search.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Primitives;

namespace TeamBeats.Modules.Documents;

/// <summary>Asks for the text of a stored PDF or image version, read outside the upload (spec 011 FR-020).</summary>
public sealed record DocumentTextRequested(Guid VersionId) : IIntegrationEvent;

/// <summary>
/// Text of PDFs and images for search (spec 011 FR-020, T026). The upload stores the file at once; the text service
/// reads it afterwards, because OCR of a long scan takes far longer than an upload should wait. A PDF's own text layer
/// is always read; scanned pages and pictures are recognized by OCR only where the tenant has <c>docs.ocr</c>. The text
/// goes on the version, and the document is indexed again.
/// </summary>
public sealed class DocumentText(
    IDbSession session,
    TenantObjectStorage storage,
    ITextRecognizer recognizer,
    IEntitlementService entitlements,
    IOutbox outbox) : IMessageHandler<DocumentTextRequested>
{
    /// <summary>The formats the text service reads: PDF, and pictures by OCR.</summary>
    public static readonly IReadOnlySet<string> Formats = new HashSet<string>(StringComparer.Ordinal) { "pdf", "png", "jpg", "jpeg" };

    /// <summary>Queues reading a new version's text when its format needs the text service.</summary>
    public async Task QueueAsync(Guid versionId, string fileName, string? text, CancellationToken ct)
    {
        if (text is null && recognizer.Available && Formats.Contains(FileTypes.ExtensionOf(fileName)))
        {
            await outbox.EnqueueAsync(new DocumentTextRequested(versionId), ct);
        }
    }

    public async Task HandleAsync(DocumentTextRequested message, CancellationToken ct)
    {
        var version = await session.QuerySingleOrDefaultAsync(
            """
            select v.object_key, v.content_type, v.document_id
            from documents.document_version v join documents.document d on d.id = v.document_id
            where v.id = @id and v.scan_state = 'clean' and v.text_extract is null and d.deleted_at is null
            """, new { id = message.VersionId },
            r => (Key: r.GetString(0), ContentType: r.GetString(1), Document: r.GetGuid(2)), ct);
        if (version.Key is null)
        {
            return;
        }
        var recognize = await entitlements.IsEnabledAsync(Capabilities.DocsOcr, ct);
        string? text;
        await using (var content = await storage.OpenReadAsync(version.Key, ct))
        {
            if (content is null)
            {
                return;
            }
            text = await recognizer.ReadAsync(content, version.ContentType, recognize, ct);
        }
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }
        // Versions never change, so the text is set once; the same file reused by a restored version gets it too.
        var updated = await session.ExecAsync(
            "update documents.document_version set text_extract = @text where object_key = @key and text_extract is null",
            new { text, key = version.Key }, ct);
        if (updated > 0)
        {
            // The index takes whichever version readers get, so a draft's text changes nothing there.
            await outbox.EnqueueAsync(new SearchItemChanged("documents", version.Document), ct);
        }
    }
}
