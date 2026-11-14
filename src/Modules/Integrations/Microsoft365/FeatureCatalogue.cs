using System.Text.Json;
using TeamBeats.Entitlements.Contracts;
using TeamBeats.Modules.Integrations.Contracts;

namespace TeamBeats.Modules.Integrations.Microsoft365;

public enum PermissionType
{
    Application,
    Delegated,
}

/// <summary>A Microsoft Graph permission a feature needs, with the resource key that explains why in plain words.</summary>
public sealed record GraphPermission(string Name, PermissionType Type, string ReasonKey);

/// <summary>A Microsoft 365 connection feature (spec 006 FR-011): its permissions, capability key and release.</summary>
public sealed record FeatureDefinition(string Key, string CapabilityKey, IReadOnlyList<GraphPermission> Permissions)
{
    public bool UsesApplicationPermissions => Permissions.Any(p => p.Type == PermissionType.Application);
}

/// <summary>
/// The R1 features of the Microsoft 365 connection. Directory sync uses read-only application permissions (spec 003
/// plan); mail and calendar use delegated permissions only (spec 018 FR-002, FR-003). Rooms arrive in R2.
/// </summary>
public static class FeatureCatalogue
{
    public static readonly IReadOnlyList<FeatureDefinition> All =
    [
        new(Microsoft365Features.DirectorySync, Capabilities.IdentityDirectorySync,
        [
            new("User.Read.All", PermissionType.Application, "integrations.permission.user_read_all"),
            new("Group.Read.All", PermissionType.Application, "integrations.permission.group_read_all"),
            new("AdministrativeUnit.Read.All", PermissionType.Application, "integrations.permission.administrative_unit_read_all"),
        ]),
        new(Microsoft365Features.Mail, Capabilities.IntegrationMicrosoft365,
        [
            new("User.Read", PermissionType.Delegated, "integrations.permission.user_read"),
            new("Mail.ReadBasic", PermissionType.Delegated, "integrations.permission.mail_read_basic"),
        ]),
        new(Microsoft365Features.Calendar, Capabilities.IntegrationMicrosoft365,
        [
            new("Calendars.ReadBasic", PermissionType.Delegated, "integrations.permission.calendars_read_basic"),
        ]),
        new(Microsoft365Features.FreeBusy, Capabilities.IntegrationMicrosoft365,
        [
            new("Calendars.ReadBasic", PermissionType.Delegated, "integrations.permission.calendars_read_basic"),
        ]),
        // Office editing (spec 011 FR-010): the employee's own OneDrive holds the copy while they edit, so only
        // Files.ReadWrite on their own files; never Files.ReadWrite.All or Sites.
        new(Microsoft365Features.OfficeEditing, Capabilities.IntegrationMicrosoft365,
        [
            new("Files.ReadWrite", PermissionType.Delegated, "integrations.permission.files_read_write"),
        ]),
        // SharePoint documents (spec 011 FR-025): the application reads only the sites an administrator granted it
        // (Sites.Selected) for listing and sync; people browse and open files with their own access.
        new(Microsoft365Features.SharePointDocuments, Capabilities.IntegrationMicrosoft365,
        [
            new("Sites.Selected", PermissionType.Application, "integrations.permission.sites_selected"),
            new("Files.Read.All", PermissionType.Delegated, "integrations.permission.files_read_all"),
        ]),
    ];

    /// <summary>The permissions an employee's own token carries for a feature.</summary>
    public static IEnumerable<GraphPermission> DelegatedFor(FeatureDefinition feature, FeatureOptions options) =>
        PermissionsFor(feature, options).Where(p => p.Type == PermissionType.Delegated);

    public static FeatureDefinition? Find(string key) => All.FirstOrDefault(f => f.Key == key);

    /// <summary>Mail with the preview line on reads <c>Mail.Read</c> instead of <c>Mail.ReadBasic</c> (spec 018 FR-002).</summary>
    public static readonly GraphPermission MailRead = new("Mail.Read", PermissionType.Delegated, "integrations.permission.mail_read");

    /// <summary>The permissions a feature needs with its current options.</summary>
    public static IReadOnlyList<GraphPermission> PermissionsFor(FeatureDefinition feature, FeatureOptions options) =>
        feature.Key == Microsoft365Features.Mail && options.PreviewLine
            ? [.. feature.Permissions.Where(p => p.Name != "Mail.ReadBasic"), MailRead]
            : feature.Permissions;

    /// <summary>Application permissions that are broader than any feature needs (spec 006 FR-013, spec 018 FR-003).</summary>
    public static bool IsBroaderThanNeeded(string role) =>
        role.StartsWith("Mail.", StringComparison.Ordinal)
        || role.StartsWith("Calendars.", StringComparison.Ordinal)
        || role.EndsWith(".ReadWrite.All", StringComparison.Ordinal)
        || role is "Directory.Read.All" or "Directory.ReadWrite.All" or "Files.Read.All" or "Sites.Read.All";
}

/// <summary>
/// Admin options of a connection feature (spec 018 plan "Data model"): Mail <c>previewLine</c> and <c>maxMessages</c>
/// (the most a placement may show, 1 to 10, default 10), Calendar <c>maxDays</c> (the longest range, 1 to 14, default 14). Other features take no options.
/// </summary>
public sealed record FeatureOptions(bool PreviewLine = false, int MaxMessages = 10, int MaxDays = 14)
{
    public static readonly FeatureOptions Default = new();

    /// <summary>Reads stored options; anything missing or out of range takes its default.</summary>
    public static FeatureOptions FromStored(string? json)
    {
        if (string.IsNullOrEmpty(json))
        {
            return Default;
        }
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        return new FeatureOptions(
            root.TryGetProperty("previewLine", out var p) && p.ValueKind == JsonValueKind.True,
            root.TryGetProperty("maxMessages", out var m) && m.TryGetInt32(out var mv) && mv is >= 1 and <= 10 ? mv : Default.MaxMessages,
            root.TryGetProperty("maxDays", out var d) && d.TryGetInt32(out var dv) && dv is >= 1 and <= 14 ? dv : Default.MaxDays);
    }

    /// <summary>The stored form for one feature: only the options that feature takes.</summary>
    public object ToStored(string feature) => feature switch
    {
        Microsoft365Features.Mail => new { previewLine = PreviewLine, maxMessages = MaxMessages },
        Microsoft365Features.Calendar => new { maxDays = MaxDays },
        _ => new { },
    };
}
