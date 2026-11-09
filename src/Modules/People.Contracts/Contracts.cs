using TeamBeats.Primitives;

namespace TeamBeats.Modules.People.Contracts;

/// <summary>
/// The reporting line (spec 010 FR-005): the single answer to "who is my manager" for the rest of the product. All
/// calls are in the current tenant; a person with no manager is valid and answers null or empty.
/// </summary>
public interface IReportingLine
{
    Task<Guid?> ManagerOfAsync(Guid userId, CancellationToken ct = default);

    /// <summary>Managers from the nearest up, at most <paramref name="levels"/> of them.</summary>
    Task<IReadOnlyList<Guid>> ChainAsync(Guid userId, int levels, CancellationToken ct = default);

    Task<IReadOnlyList<Guid>> DirectReportsAsync(Guid managerId, CancellationToken ct = default);

    /// <summary>Direct and indirect reports.</summary>
    Task<IReadOnlySet<Guid>> AllReportsAsync(Guid managerId, CancellationToken ct = default);

    /// <summary>True when <paramref name="managerId"/> is anywhere in the person's manager chain.</summary>
    Task<bool> IsManagerOfAsync(Guid managerId, Guid userId, CancellationToken ct = default);
}

/// <summary>
/// One profile field as a viewer may see it. Fields the viewer may not see are left out, never blanked.
/// <see cref="Source"/> is where the value comes from (<c>directory</c>, <c>person</c> or <c>admin</c>), so a form can keep
/// directory values read-only (spec 016 FR-004).
/// </summary>
public sealed record VisibleField(string Key, string Value, string? Source = null);

/// <summary>A person's profile reduced to what one viewer may see (spec 010 FR-003).</summary>
public sealed record VisibleProfile(Guid UserId, string DisplayName, bool Active, Guid? PrimaryUnitId, string? Location, IReadOnlyList<VisibleField> Fields)
{
    public string? Field(string key) => Fields.FirstOrDefault(f => f.Key == key)?.Value;
}

public sealed record PersonPlacement(Guid UserId, string? LocationKey, Guid? ManagerId);

/// <summary>Profiles for other modules and agent tools (spec 010 section 11, spec 027): always as the given viewer.</summary>
public interface IProfiles
{
    Task<IReadOnlyList<VisibleProfile>> ViewAsync(Guid viewerId, IReadOnlyCollection<Guid> userIds, CancellationToken ct = default);

    /// <summary>
    /// Location key and current line manager of many people in one read, for breakdowns such as the policy compliance
    /// view (spec 025 US3). Both are directory facts every member may see; people without a profile are left out.
    /// </summary>
    Task<IReadOnlyDictionary<Guid, PersonPlacement>> PlacementsAsync(IReadOnlyCollection<Guid> userIds, CancellationToken ct = default);
}

/// <summary>Field names only, never values (FR-012).</summary>
public sealed record ProfileUpdated(Guid UserId, IReadOnlyList<string> ChangedFields) : IIntegrationEvent;

public sealed record ReportingLineChanged(Guid UserId, Guid? PreviousManagerId, Guid? ManagerId, string Source) : IIntegrationEvent;

public sealed record LocationAdded(string Key) : IIntegrationEvent;
