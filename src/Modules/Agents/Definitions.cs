using TeamBeats.Modules.Agents.Contracts;
using TeamBeats.Modules.Search.Contracts;
using TeamBeats.Primitives;

namespace TeamBeats.Modules.Agents;

/// <summary>Who may use an agent (spec 020 section 12, Access): the whole tenant, or people in the listed org units.</summary>
public sealed record AgentAudience(string? Scope, List<Guid>? OrgUnits)
{
    public const string Tenant = "tenant";
    public const string Units = "org_units";
}

/// <summary>
/// The configuration of an agent version (spec 020 section 12, the R1 areas). Texts are per locale. Business settings
/// (names, instructions, tone, knowledge, audience) never change tools, autonomy, classification or approval rules
/// (FR-004): those are separate fields, checked against the tenant policy on every save.
/// </summary>
public sealed record AgentDefinition(
    Dictionary<string, string>? Name,
    Dictionary<string, string>? Description,
    string? Icon,
    string? Category,
    Dictionary<string, string>? Instructions,
    string? Tone,
    string? OutputFormat,
    List<string>? Knowledge,
    List<string>? Tools,
    AgentAudience? Audience,
    List<string>? Languages,
    Dictionary<string, string>? Escalation,
    string? Autonomy,
    string? MaxClassification,
    int? MaxToolCalls,
    Dictionary<string, string>? Purpose = null);

public static class AgentAutonomy
{
    public static readonly IReadOnlyList<string> Levels = ["L0", "L1", "L2", "L3", "L4"];

    /// <summary>What R1 builds (spec 027 US1): L3 and L4 come in later releases with their packages.</summary>
    public const string HighestBuilt = "L2";

    public static int Rank(string? level) => level is null ? -1 : Levels.ToList().IndexOf(level);
}

public static class Classifications
{
    public static readonly IReadOnlyList<string> Ordered = ["public", "internal", "confidential", "restricted"];

    public static int Rank(string? value) => value is null ? -1 : Ordered.ToList().IndexOf(value);
}

/// <summary>
/// Checks a definition against its schema, the tool registry and the tenant policy (spec 020 FR-003, FR-004, spec 027
/// FR-001, FR-017), and computes the risk class (spec 027 US9), which cannot be set by hand.
/// </summary>
public static class AgentDefinitions
{
    public static readonly IReadOnlyList<string> Languages = ["ar", "en"];
    public static readonly IReadOnlyList<string> Tones = ["neutral", "friendly", "formal"];
    public static readonly IReadOnlyList<string> OutputFormats = ["text", "bullets", "steps"];
    public static readonly IReadOnlyList<string> Categories = ["knowledge", "hr", "it", "content", "requests", "productivity", "other"];
    public const int MaxNameLength = 80;
    public const int MaxDescriptionLength = 300;
    public const int MaxPurposeLength = 1000;
    public const int MaxInstructionsLength = 4000;
    public const int MaxEscalationLength = 500;
    public const int MaxTools = 20;

    public static void Validate(AgentDefinition d, ToolRegistry tools, AgentPolicy policy)
    {
        var errors = new ValidationErrors();
        var languages = d.Languages ?? [];
        if (languages.Count == 0 || languages.Any(l => !Languages.Contains(l)) || languages.Distinct().Count() != languages.Count)
        {
            errors.Add("languages", "validation.agent.languages");
        }
        foreach (var language in languages.Where(Languages.Contains))
        {
            if (d.Name?.GetValueOrDefault(language) is not { } name || name.Trim().Length is 0 or > MaxNameLength)
            {
                errors.Add($"name.{language}", "validation.agent.name");
            }
            if (d.Description?.GetValueOrDefault(language) is { Length: > MaxDescriptionLength })
            {
                errors.Add($"description.{language}", "validation.agent.description");
            }
            if (d.Purpose?.GetValueOrDefault(language) is { Length: > MaxPurposeLength })
            {
                errors.Add($"purpose.{language}", "validation.agent.purpose");
            }
            if (d.Instructions?.GetValueOrDefault(language) is not { } text || text.Trim().Length is 0 or > MaxInstructionsLength)
            {
                errors.Add($"instructions.{language}", "validation.agent.instructions");
            }
        }
        if (d.Category is null || !Categories.Contains(d.Category))
        {
            errors.Add("category", "validation.agent.category");
        }
        if (d.Icon is not null && (d.Icon.Length > 40 || !d.Icon.All(c => char.IsAsciiLetterLower(c) || c == '-')))
        {
            errors.Add("icon", "validation.agent.icon");
        }
        if (d.Tone is null || !Tones.Contains(d.Tone))
        {
            errors.Add("tone", "validation.agent.tone");
        }
        if (d.OutputFormat is null || !OutputFormats.Contains(d.OutputFormat))
        {
            errors.Add("outputFormat", "validation.agent.output_format");
        }
        if ((d.Knowledge ?? []).Any(k => !SearchKinds.All.Contains(k)))
        {
            errors.Add("knowledge", "validation.agent.knowledge");
        }
        if (d.Audience is not { } audience || audience.Scope is not (AgentAudience.Tenant or AgentAudience.Units) ||
            (audience.Scope == AgentAudience.Units && (audience.OrgUnits is null || audience.OrgUnits.Count == 0 || audience.OrgUnits.Count > 50)))
        {
            errors.Add("audience", "validation.agent.audience");
        }

        // Autonomy: a known level, not above what R1 builds, the package and the tenant cap (spec 027 section 6).
        var rank = AgentAutonomy.Rank(d.Autonomy);
        if (rank < 0)
        {
            errors.Add("autonomy", "validation.agent.autonomy");
        }
        else if (rank > AgentAutonomy.Rank(AgentAutonomy.HighestBuilt) || rank > AgentAutonomy.Rank(policy.MaxAutonomy))
        {
            errors.Add("autonomy", "validation.agent.autonomy_above_policy");
        }

        var classification = Classifications.Rank(d.MaxClassification);
        if (classification < 0)
        {
            errors.Add("maxClassification", "validation.agent.classification");
        }
        else if (classification > Classifications.Rank(policy.MaxClassification))
        {
            errors.Add("maxClassification", "validation.agent.classification_above_policy");
        }

        // Tools: registered, never "block", allowed by the tenant, and within what the autonomy level lets the agent do.
        var names = d.Tools ?? [];
        if (names.Count > MaxTools || names.Distinct().Count() != names.Count)
        {
            errors.Add("tools", "validation.agent.tools");
        }
        var hasWrite = false;
        foreach (var name in names.Distinct())
        {
            if (tools.Find(name) is not { } tool)
            {
                errors.Add("tools", "validation.agent.tool_unknown");
                continue;
            }
            var rule = policy.RuleFor(tool.Definition);
            if (rule == ToolApprovalRules.Block || !policy.Allows(name))
            {
                errors.Add("tools", "validation.agent.tool_not_allowed");
                continue;
            }
            hasWrite |= ToolSideEffects.IsWrite(tool.Definition.SideEffect);
            var needed = tool.Definition.SideEffect switch
            {
                ToolSideEffects.Read => 0,
                ToolSideEffects.Draft => 1,
                ToolSideEffects.WriteSelf => 2,
                _ => 3,
            };
            if (rank >= 0 && needed > rank)
            {
                errors.Add("tools", "validation.agent.tool_above_autonomy");
            }
        }
        if (hasWrite && languages.Any(l => d.Escalation?.GetValueOrDefault(l) is not { } e || e.Trim().Length is 0 or > MaxEscalationLength))
        {
            errors.Add("escalation", "validation.agent.escalation");
        }
        if (d.MaxToolCalls is not { } calls || calls < 1 || calls > policy.MaxToolCalls)
        {
            errors.Add("maxToolCalls", "validation.agent.max_tool_calls");
        }
        errors.ThrowIfAny();
    }

    /// <summary>Spec 027 US9: computed from the definition, never lowered by hand.</summary>
    public static string RiskClass(AgentDefinition d, ToolRegistry tools)
    {
        var autonomy = AgentAutonomy.Rank(d.Autonomy);
        var classification = Classifications.Rank(d.MaxClassification);
        var effects = (d.Tools ?? []).Select(tools.Find).Where(t => t is not null).Select(t => t!.Definition.SideEffect).ToList();
        if (autonomy >= 3 || effects.Contains(ToolSideEffects.WriteExternal) || classification >= Classifications.Rank("restricted"))
        {
            return AgentRiskClasses.High;
        }
        if (autonomy >= 2 || classification >= Classifications.Rank("confidential") || d.Audience?.Scope == AgentAudience.Tenant)
        {
            return AgentRiskClasses.Medium;
        }
        return AgentRiskClasses.Low;
    }
}
