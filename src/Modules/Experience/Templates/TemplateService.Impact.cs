using TeamBeats.Modules.Experience.Pages;
using TeamBeats.Primitives;

namespace TeamBeats.Modules.Experience.Templates;

public sealed partial class TemplateService
{
    /// <summary>What releasing the template's draft reaches (spec 032 R2 T024, FR-054), for its designers.</summary>
    public async Task<ImpactReport> ImpactAsync(string id, ImpactReports impacts, CancellationToken ct)
    {
        await DemandDesignerAsync(ct);
        await impacts.DemandAsync(ct);
        var head = await HeadAsync(id, ct) ?? throw AppException.NotFound();
        return await impacts.ForTemplateAsync(id, head.Distribution, ct);
    }
}
