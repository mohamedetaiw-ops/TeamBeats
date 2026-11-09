using System.Text.Json;
using TeamBeats.Modules.Agents.Contracts;
using TeamBeats.Modules.AiGateway.Contracts;
using TeamBeats.Modules.Search.Contracts;

namespace TeamBeats.Modules.Agents;

/// <summary>A tool as designers see it in the tool picker (spec 020 US3 scenario 2): its side effect and approval rule.</summary>
public sealed record ToolView(string Name, string Module, int Version, string SideEffect, string DefaultRule, string Rule, string OutputClassification,
    bool Allowed, JsonElement Parameters);

/// <summary>
/// The tool registry (spec 027 T003, FR-005): every tool the modules offer to agents, by name. Tools are declared in code
/// by the module that owns the action and versioned with the product, so the registry is the same for every tenant;
/// what a tenant may use is its agent policy.
/// </summary>
public sealed class ToolRegistry
{
    private readonly Dictionary<string, IAgentTool> _tools;

    public ToolRegistry(IEnumerable<IAgentTool> tools)
    {
        _tools = new Dictionary<string, IAgentTool>(StringComparer.Ordinal);
        foreach (var tool in tools)
        {
            if (!_tools.TryAdd(tool.Definition.Name, tool))
            {
                throw new InvalidOperationException($"Agent tool {tool.Definition.Name} is registered twice.");
            }
        }
    }

    public IReadOnlyCollection<IAgentTool> All => _tools.Values;

    public IAgentTool? Find(string name) => _tools.GetValueOrDefault(name);
}

/// <summary>Helpers for tool parameter schemas and arguments.</summary>
public static class ToolJson
{
    public static JsonElement Schema(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    public static string? String(JsonElement args, string name) =>
        args.ValueKind == JsonValueKind.Object && args.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    public static IReadOnlyList<string> Strings(JsonElement args, string name) =>
        args.ValueKind == JsonValueKind.Object && args.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array
            ? [.. v.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!)]
            : [];
}

/// <summary>
/// Searches the knowledge the person may read (spec 012 US3) for an agent: passages filtered by each owner and by the
/// classification ceiling of the agent use case, returned inside the untrusted envelope with their sources for citations.
/// </summary>
public sealed class KnowledgeSearchTool(ISearchRetrieval retrieval) : IAgentTool
{
    public const string Name = "knowledge.search";
    public const int TopK = 6;

    public ToolDefinition Definition { get; } = new(
        Name, "search", 1, ToolSideEffects.Read, ToolApprovalRules.Execute,
        ToolJson.Schema(
            """
            {"type":"object","additionalProperties":false,"required":["query"],
             "properties":{"query":{"type":"string","minLength":1,"maxLength":500},
                           "kinds":{"type":"array","maxItems":6,"items":{"enum":["person","content","document","knowledge","policy","service"]}}}}
            """),
        "restricted", Idempotent: true, TimeoutSeconds: 10,
        "Search the intranet pages, documents, policies, knowledge articles and services the employee may read. Returns numbered passages with titles.");

    public async Task<ToolResult> ExecuteAsync(JsonElement arguments, ToolCallContext context, CancellationToken ct)
    {
        var query = ToolJson.String(arguments, "query")?.Trim();
        if (string.IsNullOrEmpty(query) || query.Length > 500)
        {
            return ToolResult.Failed("tool.invalid_arguments");
        }
        var kinds = ToolJson.Strings(arguments, "kinds").Where(SearchKinds.All.Contains).Distinct().ToList();
        var result = await retrieval.RetrieveAsync(new RetrievalRequest(query, AiUseCases.Agent, TopK, kinds.Count > 0 ? kinds : null), ct);
        // Passages above the agent's classification cap are left out here, at retrieval (spec 027 FR-017).
        var cap = Classifications.Rank(context.MaxClassification);
        var passages = result.Passages.Where(p => Classifications.Rank(p.Classification) is var rank && rank >= 0 && rank <= cap).ToList();
        var classification = AssistantPrompt.Name(AssistantPrompt.Highest(passages.Select(p => p.Classification), DataClassification.Public));
        var sources = passages.Select(p => new ToolSource(p.Source, p.ItemId, p.Title, p.Anchor, p.Classification)).ToList();
        return new ToolResult(true, passages.Count == 0 ? string.Empty : AssistantPrompt.Passages(passages, context.PassagesSoFar), classification, sources);
    }
}
