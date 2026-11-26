using TeamBeats.Authorization.Contracts;
using TeamBeats.Entitlements.Contracts;
using TeamBeats.Modules.Experience.Catalogue;
using TeamBeats.Modules.Experience.Components;
using TeamBeats.Primitives;

namespace TeamBeats.Modules.Experience.Templates;

/// <summary>One recommended template with the variant that suits the density and the reasons, as resource key suffixes.</summary>
public sealed record TemplateRecommendation(string TemplateId, string Variant, IReadOnlyList<string> Reasons);

public sealed record TemplateRecommendationsView(string Purpose, string Viewport, string Density, IReadOnlyList<TemplateRecommendation> Recommendations);

/// <summary>
/// Template recommendations (spec 032 FR-023, R3) from three facts the editor gives about the page: its purpose, the
/// viewport most readers use and how much content it holds. Rules only, on page metadata, with no model call and no page
/// content or user data. The answer is advice: it never creates, publishes or changes anything, and the chooser keeps
/// every template on offer.
/// </summary>
public sealed class TemplateRecommendations(IEntitlementService entitlements, IAuthorizationService authz, PlatformCatalogue catalogue, FeatureControls controls)
{
    public const int Max = 3;

    /// <summary>Templates that fit each purpose, best first.</summary>
    public static readonly IReadOnlyDictionary<string, string[]> Purposes = new Dictionary<string, string[]>(StringComparer.Ordinal)
    {
        ["general"] = ["T01", "T04", "T03"],
        ["news"] = ["T06", "T04", "T12"],
        ["department"] = ["T04", "T05", "T03"],
        ["project"] = ["T07", "T08", "T03"],
        ["knowledge"] = ["T09", "T12", "T02"],
        ["services"] = ["T05", "T07", "T09"],
        ["campaign"] = ["T11", "T06", "T10"],
        ["dashboard"] = ["T08", "T04", "T07"],
        ["onboarding"] = ["T10", "T02", "T11"],
    };

    public static readonly IReadOnlyList<string> Viewports = ["desktop", "tablet", "mobile"];
    public static readonly IReadOnlyList<string> Densities = ["light", "medium", "heavy"];

    /// <summary>Layouts that read as one column on a phone without losing structure.</summary>
    private static readonly HashSet<string> SingleColumn = new(StringComparer.Ordinal) { "L01", "L06", "L07" };

    /// <summary>The scored ranking for the facts, over the given platform templates. Pure, for tests.</summary>
    public static IReadOnlyList<TemplateRecommendation> Rank(IEnumerable<TemplateDefinition> templates, string purpose, string viewport, string density)
    {
        var fit = Purposes[purpose];
        var variant = density switch { "light" => "spacious", "heavy" => "compact", _ => "balanced" };
        var scored = new List<(TemplateDefinition T, int Score, List<string> Reasons)>();
        foreach (var t in templates)
        {
            var reasons = new List<string>();
            var score = 0;
            var rank = Array.IndexOf(fit, t.Id);
            if (rank >= 0)
            {
                score += 6 - 2 * rank;
                reasons.Add("purpose");
            }
            if (viewport == "mobile" && SingleColumn.Contains(t.Layout))
            {
                score += 3;
                reasons.Add("mobile");
            }
            var regions = t.Regions.Count;
            if (density == "heavy" && regions >= 2 || density == "light" && regions <= 2)
            {
                score += 1;
                reasons.Add(density);
            }
            if (score > 0 && reasons.Contains("purpose"))
            {
                scored.Add((t, score, reasons));
            }
        }
        return [.. scored.OrderByDescending(s => s.Score).ThenBy(s => Array.IndexOf(fit, s.T.Id)).Take(Max)
            .Select(s => new TemplateRecommendation(s.T.Id, s.T.Variants.Contains(variant) ? variant : s.T.Variants.FirstOrDefault() ?? variant, s.Reasons))];
    }

    public async Task<TemplateRecommendationsView> RecommendAsync(string? purpose, string? viewport, string? density, CancellationToken ct)
    {
        await entitlements.DemandAsync(Capabilities.ExperiencePageBuilder, ct);
        if (!(await authz.CoverageAsync(Permissions.PageEdit, ct)).Any)
        {
            throw AppException.Denied();
        }
        var errors = new ValidationErrors();
        purpose ??= "general";
        viewport ??= "desktop";
        density ??= "medium";
        if (!Purposes.ContainsKey(purpose))
        {
            errors.Add("purpose", "validation.experience.recommendation_purpose");
        }
        if (!Viewports.Contains(viewport))
        {
            errors.Add("viewport", "validation.experience.recommendation_viewport");
        }
        if (!Densities.Contains(density))
        {
            errors.Add("density", "validation.experience.recommendation_density");
        }
        errors.ThrowIfAny();
        // Only released platform page templates the tenant has not switched off; the employee home template is not a page template.
        var off = await controls.OffAsync(FeatureControlSubjects.Template, null, ct);
        var offered = catalogue.Templates.Values.Where(t => t.Id.StartsWith('T') && t.State == TemplateStates.Released && !off(t.Id));
        return new TemplateRecommendationsView(purpose, viewport, density, Rank(offered, purpose, viewport, density));
    }
}
