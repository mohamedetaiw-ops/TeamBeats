using TeamBeats.Modules.Services.Contracts;

namespace TeamBeats.Testing;

/// <summary>A module service for catalogue tests (spec 016 FR-010), standing in for the leave service of spec 022.</summary>
public sealed class TestModuleServices : IModuleServiceSource
{
    public const string LeaveKey = "test.leave";

    public IReadOnlyList<ModuleServiceRegistration> Services { get; } =
    [
        new(LeaveKey, new Dictionary<string, string> { ["en"] = "Annual leave", ["ar"] = "إجازة سنوية" },
            new Dictionary<string, string> { ["en"] = "Request time off" }, "/hr/leave/new", "hr.leave"),
    ];
}
