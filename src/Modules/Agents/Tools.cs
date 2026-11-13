using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TeamBeats.Modules.Agents.Contracts;
using TeamBeats.Modules.AiGateway.Contracts;
using TeamBeats.Modules.Search.Contracts;
using TeamBeats.Persistence;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.Agents;

/// <summary>A tool as designers see it in the tool picker (spec 020 US3 scenario 2): its side effect and approval rule.</summary>
public sealed record ToolView(string Name, string Module, int Version, string SideEffect, string DefaultRule, string Rule, string OutputClassification,
    bool Allowed, JsonElement Parameters, IReadOnlyDictionary<string, string>? Title = null);

/// <summary>
/// The tool registry (spec 027 T003, FR-005, FR-006): the product tools the modules declare in code, the same for every
/// tenant, and the tenant's own tools (connector actions registered by the integration administrator, R2). What a tenant
/// may use is its agent policy. Tenant tools are loaded once per scope by <see cref="LoadAsync"/>, which the policy
/// service calls before the policy is read; asking for a tenant tool before that, or in another tenant, is a programming error.
/// </summary>
public sealed class ToolRegistry
{
    private readonly Dictionary<string, IAgentTool> _product;
    private readonly IReadOnlyList<IAgentToolSource> _sources;
    private readonly ITenantContext _context;
    private Dictionary<string, IAgentTool>? _all;
    private Guid? _loadedFor;

    public ToolRegistry(IEnumerable<IAgentTool> tools, IEnumerable<IAgentToolSource>? sources = null, ITenantContext? context = null)
    {
        _product = new Dictionary<string, IAgentTool>(StringComparer.Ordinal);
        foreach (var tool in tools)
        {
            if (!_product.TryAdd(tool.Definition.Name, tool))
            {
                throw new InvalidOperationException($"Agent tool {tool.Definition.Name} is registered twice.");
            }
        }
        _sources = [.. sources ?? []];
        _context = context!;
        if (_sources.Count > 0 && context is null)
        {
            throw new ArgumentNullException(nameof(context), "Tenant tools need the tenant context.");
        }
        if (_sources.Count == 0)
        {
            _all = _product;
        }
    }

    /// <summary>Loads the tenant's own tools. Cheap after the first call in a scope.</summary>
    public async Task LoadAsync(CancellationToken ct)
    {
        if (_sources.Count == 0)
        {
            return;
        }
        var tenant = _context.RequireTenant();
        if (_all is not null && _loadedFor == tenant)
        {
            return;
        }
        var all = new Dictionary<string, IAgentTool>(_product, StringComparer.Ordinal);
        foreach (var source in _sources)
        {
            foreach (var tool in await source.ToolsAsync(ct))
            {
                // A product tool always wins over a tenant tool of the same name.
                all.TryAdd(tool.Definition.Name, tool);
            }
        }
        _all = all;
        _loadedFor = tenant;
    }

    public IReadOnlyCollection<IAgentTool> All => Loaded().Values;

    /// <summary>A tool by name. Product tools are found before the tenant's tools are loaded; any other name needs them.</summary>
    public IAgentTool? Find(string name) =>
        _product.TryGetValue(name, out var tool) ? tool : Loaded().GetValueOrDefault(name);

    private Dictionary<string, IAgentTool> Loaded()
    {
        if (_sources.Count == 0)
        {
            return _product;
        }
        if (_all is null || _loadedFor != _context.TenantId)
        {
            throw new InvalidOperationException("The tool registry was used before the tenant's tools were loaded.");
        }
        return _all;
    }
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

/// <summary>
/// <c>ask_teambeats</c> (spec 035 section 6, scope <c>assistant:ask</c>), for external AI clients only: a cited answer
/// from the assistant of spec 014 through the AI Gateway, over content the employee may read, counted against the tenant
/// AI budget, drawing on nothing above the client's classification cap. The knowledge agent answers; the turn is never
/// routed to an agent with tools. When the AI provider is down the answer says it is unavailable (FR-030).
/// </summary>
/// <remarks>The assistant runs agent turns over the tool registry, so it is resolved when the tool runs, not when the registry is built.</remarks>
public sealed class AskTeamBeatsTool(IServiceProvider services, IDbSession session) : IAgentTool
{
    public const string Name = "ask_teambeats";

    public ToolDefinition Definition { get; } = new(
        Name, "agents", 1, ToolSideEffects.Read, ToolApprovalRules.Execute,
        ToolJson.Schema(
            """
            {"type":"object","additionalProperties":false,"required":["question"],
             "properties":{"question":{"type":"string","minLength":1,"maxLength":2000},"conversationId":{"type":"string","format":"uuid"}}}
            """),
        "confidential", Idempotent: false, TimeoutSeconds: 60,
        "Ask the TeamBeats assistant a question about the organization's policies, documents and pages. Returns an answer with numbered citations the employee may open.",
        RequiredCapability: TeamBeats.Entitlements.Contracts.Capabilities.AiAssistant, Exposable: true);

    public async Task<ToolResult> ExecuteAsync(JsonElement arguments, ToolCallContext context, CancellationToken ct)
    {
        var question = ToolJson.String(arguments, "question")?.Trim();
        Guid? conversation = Guid.TryParse(ToolJson.String(arguments, "conversationId"), out var id) ? id : null;
        var turn = await services.GetRequiredService<AssistantService>().AskAsync(new TurnRequest(conversation, question, Knowledge: true, MaxClassification: context.MaxClassification), ct);
        var classification = await session.ScalarAsync<string>("select classification from agents.turn where id = @id", new { id = turn.Id }, ct) ?? "internal";
        var content = JsonSerializer.Serialize(new
        {
            answer = turn.Answer, outcome = turn.Outcome, reason = turn.Reason, conversationId = turn.ConversationId, language = turn.Language,
            generated = turn.Generated, citations = turn.Citations.Select(c => new { n = c.N, source = c.Source, itemId = c.ItemId, title = c.Title, link = c.Anchor }),
        }, AssistantService.Json);
        return new ToolResult(true, content, classification,
            turn.Citations.Select(c => new ToolSource(c.Source, c.ItemId, c.Title, c.Anchor, classification)).ToList());
    }
}
