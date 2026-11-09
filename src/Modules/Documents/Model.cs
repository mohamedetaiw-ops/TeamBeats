using System.Text.Json;
using TeamBeats.Persistence;

namespace TeamBeats.Modules.Documents;

/// <summary>Where a library lives (FR-001): the whole tenant, one org unit, or a spec 009 site.</summary>
public static class LibraryScopes
{
    public const string Tenant = "tenant";
    public const string OrgUnit = "org_unit";
    public const string Site = "site";

    public static readonly IReadOnlyList<string> All = [Tenant, OrgUnit, Site];
}

/// <summary>
/// The resource types whose permissions apply to documents. A folder or document that inherits uses its parent's; after
/// an explicit break it is its own, with its own ACL (FR-001).
/// </summary>
public static class Securables
{
    public const string Library = "doc_library";
    public const string Folder = "doc_folder";
    public const string Document = "doc_document";
}

/// <summary>
/// Version states (FR-004, FR-007). A pending version is the first upload of a document still missing required fields:
/// it has no number yet and only the uploader sees it.
/// </summary>
public static class VersionStates
{
    public const string Pending = "pending";
    public const string Draft = "draft";
    public const string InReview = "in_review";
    public const string Approved = "approved";
    public const string Effective = "effective";
    public const string Superseded = "superseded";
}

public static class ScanStates
{
    public const string Clean = "clean";
    public const string Infected = "infected";
    public const string Failed = "failed";
}

/// <summary>
/// File types of the default tenant allowlist (FR-003) and the content type each is served with. Active content that a
/// browser would run (HTML, SVG, scripts) and archives that hide what they hold are left out; a tenant can only narrow
/// the list, and a library only narrows the tenant's.
/// </summary>
public static class FileTypes
{
    public const int DefaultMaxFileMb = 2048;

    public static readonly IReadOnlyDictionary<string, string> ContentTypes = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["pdf"] = "application/pdf",
        ["doc"] = "application/msword",
        ["docx"] = "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        ["xls"] = "application/vnd.ms-excel",
        ["xlsx"] = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        ["ppt"] = "application/vnd.ms-powerpoint",
        ["pptx"] = "application/vnd.openxmlformats-officedocument.presentationml.presentation",
        ["odt"] = "application/vnd.oasis.opendocument.text",
        ["ods"] = "application/vnd.oasis.opendocument.spreadsheet",
        ["odp"] = "application/vnd.oasis.opendocument.presentation",
        ["rtf"] = "application/rtf",
        ["txt"] = "text/plain",
        ["csv"] = "text/csv",
        ["md"] = "text/markdown",
        ["png"] = "image/png",
        ["jpg"] = "image/jpeg",
        ["jpeg"] = "image/jpeg",
        ["gif"] = "image/gif",
        ["webp"] = "image/webp",
        ["mp4"] = "video/mp4",
        ["webm"] = "video/webm",
        ["mov"] = "video/quicktime",
    };

    public static IReadOnlyList<string> Defaults { get; } = ContentTypes.Keys.Order(StringComparer.Ordinal).ToList();

    /// <summary>The lower-case extension without the dot, or an empty string.</summary>
    public static string ExtensionOf(string fileName)
    {
        var ext = Path.GetExtension(fileName);
        return ext.Length > 1 ? ext[1..].ToLowerInvariant() : string.Empty;
    }
}

internal static class DocJson
{
    public static T Read<T>(string json) => JsonSerializer.Deserialize<T>(json, JsonDefaults.Options)!;

    public static string Write<T>(T value) => JsonSerializer.Serialize(value, JsonDefaults.Options);
}

/// <summary>Version numbers as people read them: 0.1 for a draft, 1.0 for a release.</summary>
internal static class VersionLabels
{
    public static string Of(int major, int minor) => $"{major}.{minor}";
}
