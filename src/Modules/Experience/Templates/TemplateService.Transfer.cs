using TeamBeats.Audit.Contracts;
using TeamBeats.Modules.Experience.Catalogue;
using TeamBeats.Primitives;

namespace TeamBeats.Modules.Experience.Templates;

/// <summary>
/// A template as a portable document (spec 032 R2 T028, FR-022; format in <c>docs/api/template-export.v1.md</c>). It carries
/// the structure and the policies: the platform template it is based on, names and descriptions, variants, and each region's
/// policy and lock. It never carries tenant ids, org units it is distributed to, who made it, or anything about pages.
/// </summary>
public sealed record TemplateDocument(string? Format, int? FormatVersion, string? Id, string? BasedOn, string? BasedOnVersion,
    IReadOnlyDictionary<string, string>? Names, IReadOnlyDictionary<string, string>? Descriptions, IReadOnlyList<string>? Variants,
    IReadOnlyList<TemplateRegion>? Regions);

/// <param name="Id">The id the imported template takes; when left out, the document's own id.</param>
public sealed record TemplateImportRequest(TemplateDocument? Document, string? Id);

public sealed partial class TemplateService
{
    public const string DocumentFormat = "teambeats.template";
    public const int DocumentVersion = 1;

    /// <summary>The latest version of a tenant template as a document.</summary>
    public async Task<TemplateDocument> ExportAsync(string id, CancellationToken ct)
    {
        await DemandDesignerAsync(ct);
        var head = await HeadAsync(id, ct) ?? throw AppException.NotFound();
        var (_, _, latest) = (await LatestAsync(id, forUpdate: false, ct))!.Value;
        await audit.WriteAsync(new AuditEntry(AuditActions.TemplateExported, "template", id, Changes: new { version = latest.Version }), ct);
        return new TemplateDocument(DocumentFormat, DocumentVersion, id, head.BasedOn, head.BasedOnVersion, latest.Names ?? new Dictionary<string, string>(),
            latest.Descriptions ?? new Dictionary<string, string>(), latest.Variants, latest.Regions);
    }

    /// <summary>
    /// Imports a document as a new tenant template whose first version is a draft, checked as any template draft is: the
    /// platform template must exist, the variants must be ones it offers and the regions must match its layout with valid
    /// policies. Nothing is released or distributed by an import.
    /// </summary>
    public async Task<TemplateView> ImportAsync(TemplateImportRequest request, CancellationToken ct)
    {
        await DemandDesignerAsync(ct);
        if (request.Document is not { } doc || doc.Format != DocumentFormat || doc.FormatVersion != DocumentVersion)
        {
            throw AppException.Validation("document", "validation.template.document_format");
        }
        var id = request.Id ?? doc.Id;
        await CreateAsync(new CreateTemplateRequest(id, doc.BasedOn, doc.Names, doc.Descriptions), ct);
        var view = await SaveDraftAsync(id!, new TemplateDraftRequest(doc.Names, doc.Descriptions, doc.Variants, doc.Regions), ct);
        await audit.WriteAsync(new AuditEntry(AuditActions.TemplateImported, "template", id!, Changes: new { basedOn = doc.BasedOn }), ct);
        return view;
    }
}
