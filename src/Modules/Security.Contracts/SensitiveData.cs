namespace TeamBeats.Modules.Security.Contracts;

/// <summary>Where the content is going, which decides the tenant's mode for it (spec 026 US6, section 6).</summary>
public static class SensitiveTargets
{
    /// <summary>A space with guests, or a library in one.</summary>
    public const string GuestSpace = "guest_space";

    /// <summary>Content for everyone in the tenant: audience "everyone", or a library open to the whole tenant.</summary>
    public const string Everyone = "everyone";

    public const string Other = "other";

    public static readonly IReadOnlyList<string> All = [GuestSpace, Everyone, Other];
}

/// <summary>One part of the content to scan, such as the title, the body in a language, or the file.</summary>
public sealed record SensitiveText(string Location, string Text);

/// <summary>
/// One kind of data found in one location: the type (a built-in key, or <c>custom</c> with the tenant pattern's Name),
/// how many times, and whether it may block. Never the matched value.
/// </summary>
public sealed record SensitiveFinding(string Type, string? Name, string Location, int Count, bool Blocking);

/// <summary>
/// Sensitive data detection for the modules that publish and store content (spec 026 US6, FR-015). Content calls it on
/// publish and submit, Documents on upload and on each new version.
/// </summary>
public interface ISensitiveDataScanner
{
    /// <summary>
    /// Scans the parts for the target. Nothing happens when detection is off, not in the plan, or nothing is found.
    /// In block mode a blocking finding throws <c>conflict.security.sensitive_data_blocked</c> until the data is removed.
    /// In warn mode a finding throws <c>conflict.security.sensitive_data_found</c> unless the item is classified
    /// Confidential or Restricted, or a reason is given; the reason is then audited as
    /// <c>security.sensitive_data_confirmed</c>. Both conflicts carry the findings.
    /// </summary>
    Task GuardAsync(string target, IReadOnlyList<SensitiveText> parts, string? classification, string? confirmReason, string resourceType, string resourceId,
        CancellationToken ct = default);
}
