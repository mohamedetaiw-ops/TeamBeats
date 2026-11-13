using System.Text.Json;
using TeamBeats.Entitlements.Contracts;
using TeamBeats.Modules.Agents.Contracts;

namespace TeamBeats.Modules.People;

/// <summary>
/// <c>find_person</c> (spec 035 section 6, scope <c>people:read</c>), exposable to external AI clients: directory search
/// as the employee sees it, with only the fields the field policy shows them. Directory data is Internal.
/// </summary>
public sealed class FindPersonTool(PeopleService people, IEntitlementService entitlements) : IAgentTool
{
    public const string Name = "find_person";

    public ToolDefinition Definition { get; } = new(
        Name, "people", 1, ToolSideEffects.Read, ToolApprovalRules.Execute,
        ToolArgs.Schema(
            """
            {"type":"object","additionalProperties":false,"required":["query"],
             "properties":{"query":{"type":"string","minLength":1,"maxLength":200},"limit":{"type":"integer","minimum":1,"maximum":20}}}
            """),
        "internal", Idempotent: true, TimeoutSeconds: 10,
        "Find people in the TeamBeats directory by name, job title, skill or unit. Returns names, job titles, units and profile links.",
        Capabilities.PeopleDirectory, Exposable: true);

    public async Task<ToolResult> ExecuteAsync(JsonElement arguments, ToolCallContext context, CancellationToken ct)
    {
        if (!await entitlements.IsEnabledAsync(Capabilities.PeopleDirectory, ct))
        {
            return ToolResult.Failed("tool.not_in_package");
        }
        var query = ToolArgs.String(arguments, "query")?.Trim();
        if (string.IsNullOrEmpty(query))
        {
            return ToolResult.Failed("tool.invalid_arguments");
        }
        var page = await people.SearchAsync(query, null, null, 0, ToolArgs.Int(arguments, "limit", 10), ct);
        var content = ToolArgs.Json(page.Items.Select(p => new
        {
            id = p.Id, name = p.DisplayName, jobTitle = p.JobTitle, unit = p.Unit?.Names.Values.FirstOrDefault(), location = p.Location?.Label,
            link = $"/people/{p.Id}",
        }));
        return new ToolResult(true, content, "internal", page.Items.Select(p => new ToolSource("people", p.Id, p.DisplayName, $"/people/{p.Id}", "internal")).ToList());
    }
}
