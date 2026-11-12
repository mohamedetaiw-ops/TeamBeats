using TeamBeats.Authorization.Contracts;
using TeamBeats.Entitlements.Contracts;
using TeamBeats.Modules.Search.Contracts;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Persistence;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.Spaces;

/// <summary>
/// Spaces for search (spec 029 FR-016): the name and description of each space. Who may find one is decided at query
/// time by authorization on the space, so private and secret spaces are found only by their members (FR-003), and a
/// guest finds only the spaces they are invited to.
/// </summary>
internal sealed class SpacesSearchSource(IDbSession session, ITenantContext context, IAuthorizationService authz, IEntitlementService entitlements,
    ITenantMemberships memberships, ITenantSettingsReader tenantSettings) : ISearchSource
{
    public const string Name = "spaces";

    public string Source => Name;

    public async Task<IReadOnlyList<SearchUnit>> UnitsAsync(Guid itemId, CancellationToken ct = default)
    {
        var row = await session.QuerySingleOrDefaultAsync($"select {SpaceRow.Columns} from spaces.space where id = @itemId", new { itemId }, SpaceRow.Read, ct);
        if (row is null || !row.Active)
        {
            return [];
        }
        var locale = (await tenantSettings.GetLocaleSettingsAsync(ct)).DefaultLocale;
        return [new SearchUnit("space", SearchKinds.Space, locale, row.Name, row.Description, SpacesService.Link(row.Id), row.Classification, null, null, row.UpdatedAt)];
    }

    public async Task<IReadOnlyList<Guid>> ItemIdsAsync(Guid? after, int limit, CancellationToken ct = default) =>
        await session.QueryAsync(
            "select id from spaces.space where status = 'active' and (@after::uuid is null or id > @after) order by id limit @limit",
            new { after, limit }, r => r.GetGuid(0), ct);

    public async Task<IReadOnlySet<Guid>> ReadableAsync(IReadOnlyCollection<Guid> itemIds, CancellationToken ct = default)
    {
        if (itemIds.Count == 0 || !await entitlements.IsEnabledAsync(Capabilities.SpacesProjects, ct))
        {
            return new HashSet<Guid>();
        }
        var rows = await session.QueryAsync($"select {SpaceRow.Columns} from spaces.space where id = any(@ids) and status = 'active'", new { ids = itemIds.ToArray() },
            SpaceRow.Read, ct);
        var readable = (await authz.FilterAsync(Permissions.SpacesView, rows.Select(r => r.Descriptor), ct)).Select(d => d.ResourceId).ToHashSet();
        if (context.ActorId is { } user && await memberships.GetAsync(user, ct) is { MembershipType: MembershipTypes.Guest })
        {
            var invited = await session.QueryAsync("select space_id from spaces.space_member where user_id = @user", new { user }, r => r.GetGuid(0), ct);
            readable.IntersectWith(invited);
        }
        return readable;
    }
}

/// <summary>Describes a space for authorization, so it never reads the Spaces tables (constitution XIV).</summary>
internal sealed class SpaceDescriptorProvider(IDbSession session) : IResourceDescriptorProvider
{
    public string ResourceType => SpacesService.ResourceType;

    public string ManagePermission => Permissions.SpacesOwn;

    public async Task<ResourceDescriptor?> DescribeAsync(Guid resourceId, CancellationToken ct) =>
        (await session.QuerySingleOrDefaultAsync($"select {SpaceRow.Columns} from spaces.space where id = @resourceId", new { resourceId }, SpaceRow.Read, ct))?.Descriptor;
}

/// <summary>Someone left the company: spaces whose last owner they were get a new owner (FR-014).</summary>
internal sealed class SpaceLeavers(SpacesService spaces, IEntitlementService entitlements, People.Contracts.IReportingLine reporting)
    : Messaging.IMessageHandler<TenantMembershipDisabled>
{
    public async Task HandleAsync(TenantMembershipDisabled message, CancellationToken ct)
    {
        if (!await entitlements.IsEnabledAsync(Capabilities.SpacesProjects, ct))
        {
            return;
        }
        await spaces.ReassignOwnerlessAsync(message.UserId, creator => reporting.ManagerOfAsync(creator, ct), ct);
    }
}
