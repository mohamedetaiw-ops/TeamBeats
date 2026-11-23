using TeamBeats.Entitlements.Contracts;
using TeamBeats.Modules.Documents.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Primitives;

namespace TeamBeats.Modules.Documents;

/// <summary>
/// Documents other modules link to (spec 028 lessons, spec 025 policies kept as controlled documents). Readers get the
/// version in force, and only once it is scanned clean.
/// </summary>
public sealed class DocumentReferences(DocumentFiles files, Libraries libraries, IEntitlementService entitlements, IDbSession session) : IDocumentReferences
{
    public async Task<DocumentReference?> ReadableAsync(Guid documentId, CancellationToken ct = default)
    {
        try
        {
            var (_, doc, _) = await files.VisibleAsync(documentId, ct);
            if (await files.VersionAsync(doc.Effective, ct) is not { } version || version.ScanState != ScanStates.Clean)
            {
                return null;
            }
            return new DocumentReference(doc.Id, doc.Name, version.Id, version.Label, version.FileName, version.ContentType, version.Size);
        }
        catch (AppException e) when (e.Status is 403 or 404)
        {
            return null;
        }
    }

    public async Task<ControlledDocumentText?> ControlledAsync(Guid documentId, CancellationToken ct = default)
    {
        if (!await entitlements.IsEnabledAsync(Capabilities.DocsControlled, ct))
        {
            return null;
        }
        try
        {
            var (_, doc, _) = await files.VisibleAsync(documentId, ct);
            if (await libraries.ControlledOfAsync(doc.TypeId, ct) is null || await files.VersionAsync(doc.Effective, ct) is not { } version
                || version.ScanState != ScanStates.Clean)
            {
                return null;
            }
            var text = await session.ScalarAsync<string?>("select text_extract from documents.document_version where id = @id", new { id = version.Id }, ct);
            if (string.IsNullOrWhiteSpace(text))
            {
                return null;
            }
            return new ControlledDocumentText(new DocumentReference(doc.Id, doc.Name, version.Id, version.Label, version.FileName, version.ContentType, version.Size),
                doc.Number, text, DocumentReleases.Link(doc.Id));
        }
        catch (AppException e) when (e.Status is 403 or 404)
        {
            return null;
        }
    }
}
