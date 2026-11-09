using System.Text.Json;

namespace TeamBeats.Modules.Agents;

/// <summary>
/// A platform template (spec 020 FR-005, FR-020): a versioned agent definition with the capability key that includes it.
/// The tools it needs are the tools of its definition; the library marks it "not ready" when one is missing or not allowed.
/// </summary>
public sealed record AgentTemplate(string Id, int Version, string Capability, AgentDefinition Definition);

/// <summary>
/// The platform template library. Templates are data shipped with the product (embedded JSON under <c>Templates/</c>), so
/// a new standard template needs only a new definition and its tests (SC-003). Platform templates hold no tenant data.
/// </summary>
public static class AgentTemplates
{
    private static readonly Lazy<IReadOnlyList<AgentTemplate>> Library = new(Load);

    public static IReadOnlyList<AgentTemplate> All => Library.Value;

    public static AgentTemplate? Find(string id) => All.FirstOrDefault(t => t.Id == id);

    private static IReadOnlyList<AgentTemplate> Load()
    {
        var assembly = typeof(AgentTemplates).Assembly;
        var list = new List<AgentTemplate>();
        foreach (var name in assembly.GetManifestResourceNames().Where(n => n.Contains(".Templates.", StringComparison.Ordinal) && n.EndsWith(".json", StringComparison.Ordinal)).Order())
        {
            using var stream = assembly.GetManifestResourceStream(name)!;
            list.AddRange(JsonSerializer.Deserialize<List<AgentTemplate>>(stream, AgentPolicyService.Json) ?? []);
        }
        if (list.Select(t => t.Id).Distinct().Count() != list.Count)
        {
            throw new InvalidOperationException("Agent template IDs must be unique.");
        }
        return list;
    }
}
