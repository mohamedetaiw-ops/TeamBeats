using System.Text.Json;
using TeamBeats.Entitlements.Contracts;
using TeamBeats.Modules.Agents.Contracts;

namespace TeamBeats.Modules.Documents;

/// <summary>
/// <c>get_document</c> (spec 035 section 6, scope <c>documents:read</c>), exposable to external AI clients: a document
/// the employee may read, with its metadata, classification and the version a reader gets, as data. The file itself
/// opens in TeamBeats or through the REST API with the same rights.
/// </summary>
public sealed class GetDocumentTool(DocumentFiles files) : IAgentTool
{
    public const string Name = "get_document";

    public ToolDefinition Definition { get; } = new(
        Name, "documents", 1, ToolSideEffects.Read, ToolApprovalRules.Execute,
        ToolArgs.Schema("""{"type":"object","additionalProperties":false,"required":["id"],"properties":{"id":{"type":"string","minLength":36,"maxLength":36}}}"""),
        "restricted", Idempotent: true, TimeoutSeconds: 10,
        "Read the details of one document the employee may open, by its ID: name, number, metadata, classification and the current version.",
        Capabilities.DocsCore, Exposable: true);

    public async Task<ToolResult> ExecuteAsync(JsonElement arguments, ToolCallContext context, CancellationToken ct)
    {
        if (ToolArgs.Id(arguments, "id") is not { } id)
        {
            return ToolResult.Failed("tool.invalid_arguments");
        }
        var view = await files.GetAsync(id, ct);
        var version = view.Rights.Contribute ? view.Current ?? view.Effective : view.Effective;
        var link = $"/documents/{view.Id}";
        var content = ToolArgs.Json(new
        {
            id = view.Id, libraryId = view.LibraryId, name = view.Name, number = view.Number, metadata = view.Metadata, classification = view.Classification,
            version = version is null ? null : new { label = version.Label, state = version.State, fileName = version.FileName, contentType = version.ContentType,
                size = version.Size, createdAt = version.CreatedAt },
            link,
        });
        return new ToolResult(true, content, view.Classification, [new ToolSource("documents", view.Id, view.Name, link, view.Classification)]);
    }
}
