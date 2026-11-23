namespace TeamBeats.Modules.Compliance.Contracts;

/// <summary>A control of a framework in the control library: its ID in the framework (5.10, CC6.1, 2-2) and its short title by locale.</summary>
public sealed record ControlInfo(string Code, IReadOnlyDictionary<string, string> Titles);

/// <summary>A framework of the control library (spec 026 FR-021): a platform set such as ISO 27001:2022 Annex A, or one the tenant defines, such as a national regulation.</summary>
public sealed record ControlFrameworkInfo(string Key, string Version, IReadOnlyDictionary<string, string> Names, bool Platform, IReadOnlyList<ControlInfo> Controls);

/// <summary>
/// The control library for other modules, such as policy to control mapping (spec 025 FR-015): the platform frameworks
/// and the current tenant's own. It checks no permission or package; the caller decides who sees what it builds on it.
/// </summary>
public interface IControlLibrary
{
    Task<IReadOnlyList<ControlFrameworkInfo>> FrameworksAsync(CancellationToken ct = default);
}
