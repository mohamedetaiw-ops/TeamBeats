using TeamBeats.Primitives;

namespace TeamBeats.Modules.Services.Contracts;

/// <summary>Kinds of catalogue services (spec 016 scope): a form in TeamBeats, a linked app in another system, or a module service.</summary>
public static class ServiceKinds
{
    public const string Form = "form";
    public const string Linked = "linked";
    public const string Module = "module";

    public static readonly IReadOnlyList<string> All = [Form, Linked, Module];
}

/// <summary>
/// Who a service is offered to (FR-001). Every criterion that is given must match; inside a criterion any listed value
/// matches. Org units match their whole subtree. No criterion means everyone in the tenant. The audience decides what a
/// person is offered, never what they may approve.
/// </summary>
public sealed record ServiceAudience(IReadOnlyList<Guid>? OrgUnits = null, IReadOnlyList<Guid>? Groups = null, IReadOnlyList<string>? MembershipTypes = null)
{
    public static readonly ServiceAudience Everyone = new();

    public bool IsEveryone => OrgUnits is not { Count: > 0 } && Groups is not { Count: > 0 } && MembershipTypes is not { Count: > 0 };
}

/// <summary>
/// A service a domain module provides (FR-010), registered at startup. It appears in every tenant's catalogue for
/// everyone until an administrator changes its audience or category. <see cref="LaunchRoute"/> is a path in TeamBeats;
/// <see cref="TaskSource"/> names where its tasks come from when they are not engine tasks.
/// </summary>
public sealed record ModuleServiceRegistration(
    string Key,
    IReadOnlyDictionary<string, string> Names,
    IReadOnlyDictionary<string, string>? Descriptions,
    string LaunchRoute,
    string? TaskSource = null);

/// <summary>Implemented by modules that register services in the catalogue.</summary>
public interface IModuleServiceSource
{
    IReadOnlyList<ModuleServiceRegistration> Services { get; }
}

/// <summary>Published when a service version is published, for search (spec 012). IDs only.</summary>
public sealed record ServicePublished(Guid ServiceId, int Version, string Kind) : IIntegrationEvent;

/// <summary>Published when a service is retired and leaves the catalogue.</summary>
public sealed record ServiceRetired(Guid ServiceId) : IIntegrationEvent;
