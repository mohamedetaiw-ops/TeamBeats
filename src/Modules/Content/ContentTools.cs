using System.Text.Json;
using TeamBeats.Entitlements.Contracts;
using TeamBeats.Modules.Agents.Contracts;
using TeamBeats.Modules.Content.Contracts;

namespace TeamBeats.Modules.Content;

/// <summary>
/// <c>get_page</c> (spec 035 section 6, scope <c>content:read</c>), exposable to external AI clients: one live news item,
/// announcement or page the employee may read, in their language, as data with its source and classification.
/// </summary>
public sealed class GetPageTool(Items items) : IAgentTool
{
    public const string Name = "get_page";

    public ToolDefinition Definition { get; } = new(
        Name, "content", 1, ToolSideEffects.Read, ToolApprovalRules.Execute,
        ToolArgs.Schema("""{"type":"object","additionalProperties":false,"required":["id"],"properties":{"id":{"type":"string","minLength":36,"maxLength":36}}}"""),
        "restricted", Idempotent: true, TimeoutSeconds: 10,
        "Read one TeamBeats page, news item or announcement the employee may read, by its ID. Returns the title, summary and body text.",
        Capabilities.ContentCore, Exposable: true);

    public async Task<ToolResult> ExecuteAsync(JsonElement arguments, ToolCallContext context, CancellationToken ct)
    {
        if (ToolArgs.Id(arguments, "id") is not { } id)
        {
            return ToolResult.Failed("tool.invalid_arguments");
        }
        var view = await items.ReadAsync(id, ct);
        var link = $"/content/read/{view.Id}";
        var content = ToolArgs.Json(new
        {
            id = view.Id, type = view.Type, locale = view.Locale, title = view.Title, summary = view.Summary, text = BodyValidator.PlainText(view.Body),
            version = view.Version, publishedAt = view.PublishedAt, updatedAt = view.UpdatedAt, expired = view.Expired, link, classification = view.Classification,
        });
        return new ToolResult(true, content, view.Classification, [new ToolSource("content", view.Id, view.Title, link, view.Classification)]);
    }
}

/// <summary>
/// <c>list_news</c> (spec 035 section 6, scope <c>content:read</c>): the employee's news feed as in TeamBeats, newest
/// first, leaving out items above the client's classification cap.
/// </summary>
public sealed class ListNewsTool(ContentFeed feed) : IAgentTool
{
    public const string Name = "list_news";

    public ToolDefinition Definition { get; } = new(
        Name, "content", 1, ToolSideEffects.Read, ToolApprovalRules.Execute,
        ToolArgs.Schema(
            """
            {"type":"object","additionalProperties":false,
             "properties":{"siteId":{"type":"string","minLength":36,"maxLength":36},"type":{"enum":["news","announcement","page"]},"limit":{"type":"integer","minimum":1,"maximum":50}}}
            """),
        "restricted", Idempotent: true, TimeoutSeconds: 10,
        "List the latest news, announcements and pages in the employee's TeamBeats feed, pinned first, with titles, summaries and IDs.",
        Capabilities.ContentCore, Exposable: true);

    public async Task<ToolResult> ExecuteAsync(JsonElement arguments, ToolCallContext context, CancellationToken ct)
    {
        var page = await feed.PageAsync(ToolArgs.Id(arguments, "siteId"), ToolArgs.String(arguments, "type"), null, ToolArgs.Int(arguments, "limit", 10), ct);
        var shown = page.Items.Where(i => ToolArgs.Within(i.Classification, context.MaxClassification)).ToList();
        var content = ToolArgs.Json(shown.Select(i => new
        {
            id = i.Id, type = i.Type, locale = i.Locale, title = i.Title, summary = i.Summary, publishedAt = i.PublishedAt, pinned = i.Pinned,
            link = $"/content/read/{i.Id}", classification = i.Classification,
        }));
        return new ToolResult(true, content, ToolArgs.Highest(shown.Select(i => i.Classification)),
            shown.Select(i => new ToolSource("content", i.Id, i.Title, $"/content/read/{i.Id}", i.Classification)).ToList());
    }
}

/// <summary>
/// <c>draft_news</c> (spec 035 section 6, scope <c>content:draft</c>), a write tool for external AI clients only: a news
/// draft in the employee's name with their authoring rights at the site, never published. The gateway runs it only once
/// the employee confirmed the exact text inside TeamBeats; the author then opens the draft to review and publish it.
/// </summary>
public sealed class DraftNewsTool(Items items, TeamBeats.Localization.RequestLocale locale) : IAgentTool
{
    public const string Name = "draft_news";

    public ToolDefinition Definition { get; } = new(
        Name, "content", 1, ToolSideEffects.Draft, ToolApprovalRules.Confirm,
        ToolArgs.Schema(
            """
            {"type":"object","additionalProperties":false,"required":["title","text"],
             "properties":{"siteId":{"type":"string","minLength":36,"maxLength":36},"locale":{"type":"string","minLength":2,"maxLength":10},
                           "title":{"type":"string","minLength":1,"maxLength":200},"summary":{"type":"string","maxLength":500},
                           "text":{"type":"string","minLength":1,"maxLength":20000}}}
            """),
        "internal", Idempotent: false, TimeoutSeconds: 15,
        "Draft a news item in TeamBeats in the employee's name. Give a title and the text, with paragraphs separated by blank lines. It is never published: the employee confirms the draft in TeamBeats, then reviews and publishes it there.",
        Capabilities.ContentCore, Exposable: true);

    public async Task<ToolResult> ExecuteAsync(JsonElement arguments, ToolCallContext context, CancellationToken ct)
    {
        var paragraphs = (ToolArgs.String(arguments, "text") ?? string.Empty).Replace("\r\n", "\n", StringComparison.Ordinal)
            .Split("\n\n", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(p => new Block(BlockTypes.Paragraph, [new Inline(p)])).ToList();
        var language = ToolArgs.String(arguments, "locale") ?? locale.Locale;
        var view = await items.CreateAsync(new ItemRequest(ToolArgs.Id(arguments, "siteId"), ContentTypes.News, null, null, null, null, null, null, null,
            new Dictionary<string, VariantInput> { [language] = new(ToolArgs.String(arguments, "title"), ToolArgs.String(arguments, "summary"), new BodyDocument(paragraphs)) },
            null, null), ct);
        var link = $"/content/items/{view.Id}";
        var title = view.Variants.TryGetValue(view.PrimaryLocale, out var v) ? v.Title ?? string.Empty : string.Empty;
        var content = ToolArgs.Json(new { id = view.Id, siteId = view.SiteId, status = view.Status, locale = view.PrimaryLocale, title, link, published = false });
        return new ToolResult(true, content, "internal", [new ToolSource("content", view.Id, title, link, "internal")]);
    }
}
