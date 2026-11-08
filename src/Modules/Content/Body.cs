using System.Text.Json;
using System.Text.Json.Serialization;
using TeamBeats.Primitives;

namespace TeamBeats.Modules.Content;

/// <summary>
/// The body of a content variant: a structured document of blocks with inline runs, never HTML (spec 009 plan, spec 026
/// FR-025). The API accepts only the blocks and marks listed here, so there is nothing to sanitize and nothing that runs.
/// </summary>
public sealed record BodyDocument(IReadOnlyList<Block> Blocks)
{
    public static readonly BodyDocument Empty = new([]);
}

/// <summary>One block. Which fields count depends on <see cref="Type"/>; the validator refuses fields a type does not use.</summary>
public sealed record Block(
    string Type,
    IReadOnlyList<Inline>? Children = null,
    int? Level = null,
    bool? Ordered = null,
    IReadOnlyList<IReadOnlyList<Inline>>? Items = null,
    IReadOnlyList<IReadOnlyList<IReadOnlyList<Inline>>>? Rows = null,
    Guid? AssetId = null,
    string? Alt = null,
    string? Url = null,
    string? Text = null);

/// <summary>A run of text with marks and an optional link.</summary>
public sealed record Inline(string Text, IReadOnlyList<string>? Marks = null, string? Link = null);

public static class BlockTypes
{
    public const string Paragraph = "paragraph";
    public const string Heading = "heading";
    public const string List = "list";
    public const string Quote = "quote";
    public const string Table = "table";
    public const string Image = "image";
    public const string Link = "link";
    public const string Embed = "embed";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal) { Paragraph, Heading, List, Quote, Table, Image, Link, Embed };
}

public static class Marks
{
    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal) { "bold", "italic" };
}

/// <summary>Checks a body document against the allowed blocks, marks, links, embeds and size limits.</summary>
public static class BodyValidator
{
    public const int MaxBlocks = 500;
    public const int MaxCharacters = 100_000;
    public const int MaxRuns = 200;
    public const int MaxListItems = 200;
    public const int MaxTableRows = 100;
    public const int MaxTableColumns = 12;
    public const int MaxUrlLength = 2000;
    public const int MaxAltLength = 300;

    /// <summary>Adds an error under <paramref name="field"/> for the first problem found; one key per kind of problem.</summary>
    public static void Validate(BodyDocument? body, IReadOnlySet<string> embedHosts, string field, ValidationErrors errors)
    {
        if (body?.Blocks is not { } blocks)
        {
            return;
        }
        if (blocks.Count > MaxBlocks)
        {
            errors.Add(field, "validation.content.body_too_large");
            return;
        }
        var characters = 0;
        string? problem = null;
        foreach (var block in blocks)
        {
            problem ??= CheckBlock(block, embedHosts, ref characters);
            if (characters > MaxCharacters)
            {
                problem = "validation.content.body_too_large";
            }
            if (problem is not null)
            {
                break;
            }
        }
        if (problem is not null)
        {
            errors.Add(field, problem);
        }
    }

    private static string? CheckBlock(Block? b, IReadOnlySet<string> embedHosts, ref int characters)
    {
        if (b is null || !BlockTypes.All.Contains(b.Type ?? string.Empty))
        {
            return "validation.content.body_block";
        }
        (bool children, bool level, bool items, bool rows, bool asset, bool url, bool text) uses = b.Type switch
        {
            BlockTypes.Paragraph or BlockTypes.Quote => (true, false, false, false, false, false, false),
            BlockTypes.Heading => (true, true, false, false, false, false, false),
            BlockTypes.List => (false, false, true, false, false, false, false),
            BlockTypes.Table => (false, false, false, true, false, false, false),
            BlockTypes.Image => (false, false, false, false, true, false, false),
            BlockTypes.Link => (false, false, false, false, false, true, true),
            _ => (false, false, false, false, false, true, false),
        };
        if ((!uses.children && b.Children is not null) || (!uses.level && b.Level is not null) || (!uses.items && (b.Items is not null || b.Ordered is not null))
            || (!uses.rows && b.Rows is not null) || (!uses.asset && (b.AssetId is not null || b.Alt is not null))
            || (!uses.url && b.Url is not null) || (!uses.text && b.Text is not null))
        {
            return "validation.content.body_block";
        }
        switch (b.Type)
        {
            case BlockTypes.Paragraph or BlockTypes.Quote:
                return CheckRuns(b.Children, ref characters);
            case BlockTypes.Heading:
                return b.Level is not (2 or 3 or 4) ? "validation.content.body_block" : CheckRuns(b.Children, ref characters);
            case BlockTypes.List:
                if (b.Items is not { Count: > 0 and <= MaxListItems } items)
                {
                    return "validation.content.body_block";
                }
                foreach (var item in items)
                {
                    if (CheckRuns(item, ref characters) is { } p)
                    {
                        return p;
                    }
                }
                return null;
            case BlockTypes.Table:
                if (b.Rows is not { Count: > 0 and <= MaxTableRows } rows || rows.Any(r => r is not { Count: > 0 and <= MaxTableColumns }))
                {
                    return "validation.content.body_block";
                }
                foreach (var cell in rows.SelectMany(r => r))
                {
                    if (CheckRuns(cell, ref characters, allowEmpty: true) is { } p)
                    {
                        return p;
                    }
                }
                return null;
            case BlockTypes.Image:
                if (b.AssetId is not { } asset || asset == Guid.Empty)
                {
                    return "validation.content.body_block";
                }
                characters += b.Alt?.Length ?? 0;
                return b.Alt is { Length: > MaxAltLength } ? "validation.content.body_block" : null;
            case BlockTypes.Link:
                characters += b.Text?.Length ?? 0;
                return string.IsNullOrWhiteSpace(b.Text) || b.Text.Length > 500 ? "validation.content.body_block"
                    : !IsSafeLink(b.Url) ? "validation.content.body_link" : null;
            default:
                return !IsAllowedEmbed(b.Url, embedHosts) ? "validation.content.body_embed" : null;
        }
    }

    private static string? CheckRuns(IReadOnlyList<Inline>? runs, ref int characters, bool allowEmpty = false)
    {
        if (runs is null || (!allowEmpty && runs.Count == 0) || runs.Count > MaxRuns)
        {
            return "validation.content.body_block";
        }
        foreach (var run in runs)
        {
            if (run?.Text is null || (run.Marks?.Any(m => !Marks.All.Contains(m ?? string.Empty)) ?? false) || (run.Marks?.Count ?? 0) > Marks.All.Count)
            {
                return "validation.content.body_block";
            }
            if (run.Link is not null && !IsSafeLink(run.Link))
            {
                return "validation.content.body_link";
            }
            characters += run.Text.Length;
        }
        return null;
    }

    /// <summary>Links are https, http or mailto addresses, or paths inside the tenant host. Nothing else, so no script URLs.</summary>
    public static bool IsSafeLink(string? url)
    {
        if (string.IsNullOrWhiteSpace(url) || url.Length > MaxUrlLength || url.Any(char.IsControl))
        {
            return false;
        }
        if (url.StartsWith('/'))
        {
            return !url.StartsWith("//", StringComparison.Ordinal) && !url.Contains('\\');
        }
        return Uri.TryCreate(url, UriKind.Absolute, out var uri)
            && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeMailto);
    }

    /// <summary>An embed is an https address on a host of the tenant allowlist, or a subdomain of one.</summary>
    public static bool IsAllowedEmbed(string? url, IReadOnlySet<string> hosts) =>
        url is { Length: <= MaxUrlLength } && Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps
        && string.IsNullOrEmpty(uri.UserInfo)
        && hosts.Any(h => uri.Host.Equals(h, StringComparison.OrdinalIgnoreCase) || uri.Host.EndsWith("." + h, StringComparison.OrdinalIgnoreCase));

    /// <summary>The visible text of a body, for hashes of what a reader was shown and for compare.</summary>
    public static string PlainText(BodyDocument body)
    {
        var parts = new List<string>();
        foreach (var b in body.Blocks)
        {
            switch (b.Type)
            {
                case BlockTypes.List:
                    parts.AddRange(b.Items!.Select(Runs));
                    break;
                case BlockTypes.Table:
                    parts.AddRange(b.Rows!.Select(r => string.Join(" | ", r.Select(Runs))));
                    break;
                case BlockTypes.Image:
                    parts.Add(b.Alt ?? string.Empty);
                    break;
                case BlockTypes.Link:
                    parts.Add(b.Text ?? string.Empty);
                    break;
                case BlockTypes.Embed:
                    parts.Add(b.Url ?? string.Empty);
                    break;
                default:
                    parts.Add(Runs(b.Children ?? []));
                    break;
            }
        }
        return string.Join("\n", parts);

        static string Runs(IReadOnlyList<Inline> runs) => string.Concat(runs.Select(r => r.Text));
    }
}

/// <summary>JSON options for stored content: camelCase, nulls left out so the stored body stays small and stable.</summary>
internal static class ContentJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static T Read<T>(string json) => JsonSerializer.Deserialize<T>(json, Options)!;

    public static string Write<T>(T value) => JsonSerializer.Serialize(value, Options);
}
