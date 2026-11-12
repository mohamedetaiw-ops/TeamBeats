using TeamBeats.Modules.Documents.Contracts;
using TeamBeats.Primitives;

namespace TeamBeats.Modules.Documents;

/// <summary>Documents other modules link to (spec 028 lessons). Readers get the version in force, and only once it is scanned clean.</summary>
public sealed class DocumentReferences(DocumentFiles files) : IDocumentReferences
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
}
