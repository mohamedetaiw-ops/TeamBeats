using TeamBeats.Audit.Contracts;
using TeamBeats.Entitlements.Contracts;
using TeamBeats.Modules.Engagement.Contracts;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Primitives;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.Engagement;

/// <summary>
/// The discussion of a project or team space (spec 029 FR-006): a closed community with the general channel and no
/// stored members, as the space roles decide who is in it. Spaces has already decided the caller may create or change
/// the space. The name is stored for every enabled language, as the space has one name.
/// </summary>
public sealed class SpaceChannels(IDbSession session, ITenantContext context, IEntitlementService entitlements, ITenantSettingsReader tenantSettings,
    Communities communities, IAuditWriter audit, IClock clock) : ISpaceChannels
{
    private async Task<Jsonb> NamesAsync(string name, CancellationToken ct)
    {
        var locales = await tenantSettings.GetLocaleSettingsAsync(ct);
        return Jsonb.From(locales.EnabledLocales.Append(locales.DefaultLocale).Distinct().ToDictionary(l => l, _ => name.Trim()));
    }

    public async Task<Guid?> CreateAsync(Guid spaceId, string name, string classification, CancellationToken ct = default)
    {
        if (!await entitlements.IsEnabledAsync(Capabilities.EngagementCommunities, ct))
        {
            return null;
        }
        var id = Ids.New();
        var now = clock.UtcNow;
        await session.ExecAsync(
            """
            insert into engagement.community (tenant_id, id, names, descriptions, kind, space_id, classification, created_by, created_at, updated_at)
            values (platform.current_tenant_id(), @id, @names, '{}'::jsonb, 'closed', @spaceId, @classification, @user, @now, @now)
            """, new { id, names = await NamesAsync(name, ct), spaceId, classification, user = context.ActorId, now }, ct);
        await session.ExecAsync(
            "insert into engagement.channel (tenant_id, id, community_id, key, names, position, created_at) values (platform.current_tenant_id(), @channel, @id, @key, '{}'::jsonb, 0, @now)",
            new { channel = Ids.New(), id, key = Communities.GeneralChannel, now }, ct);
        await audit.WriteAsync(new AuditEntry(AuditActions.EngagementCommunityCreated, "community", id.ToString(),
            Changes: new { kind = CommunityKinds.Closed, spaceId, classification }), ct);
        return id;
    }

    public async Task UpdateAsync(Guid communityId, string name, string classification, CancellationToken ct = default)
    {
        await session.ExecAsync(
            """
            update engagement.community set names = @names, classification = @classification, updated_at = @now, row_version = row_version + 1
            where id = @communityId and space_id is not null
            """, new { communityId, names = await NamesAsync(name, ct), classification, now = clock.UtcNow }, ct);
        await communities.ReindexAsync(communityId, ct);
    }

    public async Task SetArchivedAsync(Guid communityId, bool archived, CancellationToken ct = default)
    {
        await session.ExecAsync(
            "update engagement.community set status = @status, updated_at = @now, row_version = row_version + 1 where id = @communityId and space_id is not null",
            new { communityId, status = archived ? "archived" : "active", now = clock.UtcNow }, ct);
        await communities.ReindexAsync(communityId, ct);
    }
}
