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
    ];

    public static FeatureDefinition? Find(string key) => All.FirstOrDefault(f => f.Key == key);

    /// <summary>Application permissions that are broader than any feature needs (spec 006 FR-013, spec 018 FR-003).</summary>
    public static bool IsBroaderThanNeeded(string role) =>
        role.StartsWith("Mail.", StringComparison.Ordinal)
        || role.StartsWith("Calendars.", StringComparison.Ordinal)
        || role.EndsWith(".ReadWrite.All", StringComparison.Ordinal)
        || role is "Directory.Read.All" or "Directory.ReadWrite.All" or "Files.Read.All" or "Sites.Read.All";
}
