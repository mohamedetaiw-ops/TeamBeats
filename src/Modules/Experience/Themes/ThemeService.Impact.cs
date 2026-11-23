using TeamBeats.Authorization.Contracts;
using TeamBeats.Modules.Experience.Pages;

namespace TeamBeats.Modules.Experience.Themes;

public sealed partial class ThemeService
{
    /// <summary>What a theme assignment at a scope reaches (spec 032 R2 T024, spec 033), for those who publish themes there.</summary>
    public async Task<ImpactReport> ImpactAsync(string scope, ImpactReports impacts, CancellationToken ct)
    {
        var unit = await ScopeAsync(scope, Permissions.ThemePublish, ct);
        await impacts.DemandAsync(ct);
        return await impacts.ForScopeAsync(unit, ct);
    }
}
