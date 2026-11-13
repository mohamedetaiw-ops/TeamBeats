namespace TeamBeats.Modules.PublicApi.Contracts;

/// <summary>The resource types a public event is about, as its notification names them.</summary>
public static class PublicResourceTypes
{
    public const string Content = "content";
    public const string Document = "document";
    public const string Policy = "policy";
    public const string Request = "request";
    public const string Person = "person";
    public const string OrgUnit = "org_unit";
}

/// <summary>
/// One event of the public event catalogue (spec 035 section 12): its type and schema version, the resource it is
/// about, the scope an app needs to read that resource, and the public API path of the resource.
/// </summary>
public sealed record PublicEventDefinition(string Type, int Version, string ResourceType, string ReadScope, string PathTemplate)
{
    /// <summary>The resource URL path under <c>/api/public/v1</c>.</summary>
    public string PathFor(Guid resourceId) => PublicEndpoints.Version1 + PathTemplate.Replace("{id}", resourceId.ToString("D"), StringComparison.Ordinal);
}

/// <summary>
/// The public event catalogue (R2). Each public event is derived from an internal event and carries only identifiers
/// and a link (FR-018); internal events are never sent as they are. Version 1 of every schema is
/// <see cref="PublicEventData"/>.
/// </summary>
public static class PublicEventCatalogue
{
    public const string ContentPublished = "ContentPublished";
    public const string DocumentVersionPublished = "DocumentVersionPublished";
    public const string PolicyPublished = "PolicyPublished";
    public const string RequestSubmitted = "RequestSubmitted";
    public const string RequestCompleted = "RequestCompleted";
    public const string ProfileUpdated = "ProfileUpdated";
    public const string OrganizationUnitMoved = "OrganizationUnitMoved";

    public static readonly IReadOnlyList<PublicEventDefinition> All =
    [
        new(ContentPublished, 1, PublicResourceTypes.Content, ScopeCatalogue.ContentRead, "/content/{id}"),
        new(DocumentVersionPublished, 1, PublicResourceTypes.Document, ScopeCatalogue.DocumentsRead, "/documents/{id}"),
        new(PolicyPublished, 1, PublicResourceTypes.Policy, ScopeCatalogue.PoliciesRead, "/policies/{id}"),
        new(RequestSubmitted, 1, PublicResourceTypes.Request, ScopeCatalogue.RequestsRead, "/requests/{id}"),
        new(RequestCompleted, 1, PublicResourceTypes.Request, ScopeCatalogue.RequestsRead, "/requests/{id}"),
        new(ProfileUpdated, 1, PublicResourceTypes.Person, ScopeCatalogue.PeopleRead, "/people/{id}"),
        new(OrganizationUnitMoved, 1, PublicResourceTypes.OrgUnit, ScopeCatalogue.OrgRead, "/org-units/{id}"),
    ];

    public static PublicEventDefinition? Find(string? type) => All.FirstOrDefault(e => e.Type == type);
}

/// <summary>
/// The <c>data</c> of a public event notification, schema version 1: identifiers and a link only, at most 4 KB. The
/// notification itself adds the delivery ID, the event type and the time. The app reads the resource through the API
/// with its own authorization at that moment (US4 scenario 3).
/// </summary>
public sealed record PublicEventData(Guid EventId, int Version, string ResourceType, Guid ResourceId, string ResourceUrl);

/// <summary>
/// Whether the principal in context may read one resource through the public API, as the resource's own endpoint would
/// decide. The owning module implements it for its resource type, so an event about a resource an app cannot read is not
/// sent to that app and a notification never reveals that a hidden resource exists (FR-018, US4 scenario 2).
/// </summary>
public interface IPublicResourceVisibility
{
    string ResourceType { get; }

    Task<bool> VisibleAsync(Guid resourceId, CancellationToken ct = default);
}

/// <summary>
/// A visibility check by the resource's own read: the module's read for its public endpoint runs as the principal in
/// context and the answer is whether it would succeed. A read that is refused, not found or gone means not visible.
/// </summary>
public abstract class PublicReadVisibility : IPublicResourceVisibility
{
    public abstract string ResourceType { get; }

    protected abstract Task ReadAsync(Guid resourceId, CancellationToken ct);

    public async Task<bool> VisibleAsync(Guid resourceId, CancellationToken ct = default)
    {
        try
        {
            await ReadAsync(resourceId, ct);
            return true;
        }
        catch (TeamBeats.Primitives.AppException e) when (e.Status is 401 or 403 or 404 or 410)
        {
            return false;
        }
    }
}
