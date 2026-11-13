using System.Text.Json;
using TeamBeats.Entitlements.Contracts;
using TeamBeats.Modules.Agents.Contracts;
using TeamBeats.Modules.Search.Contracts;

namespace TeamBeats.Modules.Search;

/// <summary>
/// <c>search</c> (spec 035 section 6, scope <c>search:query</c>), exposable to external AI clients: search across what
/// the employee may read, filtered by each owning module as in TeamBeats (spec 004 FR-012), leaving out results above
/// the client's classification cap (spec 035 US3 scenario 3). Each result carries its source and classification.
/// </summary>
public sealed class SearchTool(SearchService search) : IAgentTool
{
    public const string Name = "search";

    public ToolDefinition Definition { get; } = new(
        Name, "search", 1, ToolSideEffects.Read, ToolApprovalRules.Execute,
        ToolArgs.Schema(
            """
            {"type":"object","additionalProperties":false,"required":["query"],
             "properties":{"query":{"type":"string","minLength":1,"maxLength":200},"kind":{"type":"string","maxLength":40}}}
            """),
        "restricted", Idempotent: true, TimeoutSeconds: 10,
        "Search TeamBeats pages, news, documents, policies, people and services the employee may read. Returns titles, snippets, kinds and links.",
        Capabilities.SearchCore, Exposable: true);

    public async Task<ToolResult> ExecuteAsync(JsonElement arguments, ToolCallContext context, CancellationToken ct)
    {
        var query = ToolArgs.String(arguments, "query")?.Trim();
        if (string.IsNullOrEmpty(query))
        {
            return ToolResult.Failed("tool.invalid_arguments");
        }
        var kind = ToolArgs.String(arguments, "kind");
        if (kind is not null && !SearchKinds.All.Contains(kind))
        {
            return ToolResult.Failed("tool.invalid_arguments");
        }
        var page = await search.SearchAsync(query, kind, null, null, 1, ct);
        var shown = page.Results.Where(r => ToolArgs.Within(r.Classification, context.MaxClassification)).ToList();
        var content = ToolArgs.Json(new
        {
            query = page.Query, degraded = page.Degraded,
            results = shown.Select(r => new { id = r.ItemId, source = r.Source, kind = r.Kind, title = r.Title, snippet = r.Snippet, link = r.Link,
                locale = r.Locale, updatedAt = r.UpdatedAt, classification = r.Classification }),
        });
        return new ToolResult(true, content, ToolArgs.Highest(shown.Select(r => r.Classification)),
            shown.Select(r => new ToolSource(r.Source, r.ItemId, r.Title, r.Link, r.Classification)).ToList());
    }
}
