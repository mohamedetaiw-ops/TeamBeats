using TeamBeats.Audit.Contracts;
using TeamBeats.Messaging;
using TeamBeats.Modules.PublicApi.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Primitives;

namespace TeamBeats.Modules.PublicApi;

/// <summary>What happened when a refresh token or an authorization code was presented.</summary>
public enum Redemption
{
    Accepted,

    /// <summary>The code or refresh token was used before: the family is revoked now (FR-006).</summary>
    Reused,

    /// <summary>The family was revoked or has expired.</summary>
    Revoked,
}

/// <summary>A token family as access token checks read it.</summary>
public sealed record FamilyState(Guid AppId, Guid UserId, bool Revoked);

/// <summary>
/// Token families (spec 035 FR-006, FR-007): one sign-in of a person to an app. Its authorization code is redeemed once and
/// each refresh replaces the generation, so a refresh token that comes back a second time is reuse and the whole family is
/// revoked. Revoking a family refuses its refresh tokens at once and its access tokens at the next check.
/// </summary>
public sealed class TokenFamilies(IDbSession session, IAuditWriter audit, IOutbox outbox, IClock clock)
{
    public async Task<Guid> CreateAsync(Guid appId, Guid userId, IReadOnlyList<string> scopes, string resource, TimeSpan lifetime, CancellationToken ct)
    {
        var id = Ids.New();
        await session.ExecAsync(
            """
            insert into publicapi.token_family (tenant_id, id, app_id, user_id, scopes, resource, expires_at)
            values (platform.current_tenant_id(), @id, @appId, @userId, @scopes, @resource, @expires)
            """, new { id, appId, userId, scopes = scopes.ToArray(), resource, expires = clock.UtcNow + lifetime }, ct);
        return id;
    }

    /// <summary>Redeems the authorization code of a family and sets its first refresh generation.</summary>
    public async Task<Redemption> RedeemCodeAsync(Guid familyId, string generation, TimeSpan refreshLifetime, CancellationToken ct)
    {
        var changed = await session.ExecAsync(
            """
            update publicapi.token_family set code_redeemed_at = @now, generation_hash = @generation, last_used_at = @now, expires_at = @expires
            where id = @familyId and code_redeemed_at is null and revoked_at is null and expires_at > @now
            """, new { familyId, generation = ApiRules.HashGeneration(generation), now = clock.UtcNow, expires = clock.UtcNow + refreshLifetime }, ct);
        return changed > 0 ? Redemption.Accepted : await RefuseAsync(familyId, ct);
    }

    /// <summary>Replaces the refresh generation; the presented one must be the current one.</summary>
    public async Task<Redemption> RotateAsync(Guid familyId, string presented, string next, TimeSpan refreshLifetime, CancellationToken ct)
    {
        var changed = await session.ExecAsync(
            """
            update publicapi.token_family set generation_hash = @next, last_used_at = @now, expires_at = @expires
            where id = @familyId and generation_hash = @presented and revoked_at is null and expires_at > @now
            """,
            new
            {
                familyId, presented = ApiRules.HashGeneration(presented), next = ApiRules.HashGeneration(next), now = clock.UtcNow,
                expires = clock.UtcNow + refreshLifetime,
            }, ct);
        return changed > 0 ? Redemption.Accepted : await RefuseAsync(familyId, ct);
    }

    public Task<FamilyState?> StateAsync(Guid familyId, CancellationToken ct) =>
        session.QuerySingleOrDefaultAsync("select app_id, user_id, revoked_at is not null or expires_at <= now() from publicapi.token_family where id = @familyId",
            new { familyId }, r => new FamilyState(r.GetGuid(0), r.GetGuid(1), r.GetBoolean(2)), ct);

    public async Task<bool> RevokeAsync(Guid familyId, string reason, CancellationToken ct)
    {
        var row = await session.QuerySingleOrDefaultAsync(
            "update publicapi.token_family set revoked_at = now(), revoked_reason = @reason where id = @familyId and revoked_at is null returning app_id, user_id",
            new { familyId, reason }, r => (App: r.GetGuid(0), User: r.GetGuid(1)), ct);
        if (row == default)
        {
            return false;
        }
        await RecordAsync(familyId, row.App, row.User, reason, ct);
        return true;
    }

    /// <summary>Revokes every live family of an app, or of one person with the app.</summary>
    public async Task<int> RevokeForAppAsync(Guid appId, Guid? userId, string reason, CancellationToken ct)
    {
        var rows = await session.QueryAsync(
            """
            update publicapi.token_family set revoked_at = now(), revoked_reason = @reason
            where app_id = @appId and (@userId::uuid is null or user_id = @userId) and revoked_at is null
            returning id, user_id
            """, new { appId, userId, reason }, r => (Id: r.GetGuid(0), User: r.GetGuid(1)), ct);
        foreach (var row in rows)
        {
            await RecordAsync(row.Id, appId, row.User, reason, ct);
        }
        return rows.Count;
    }

    /// <summary>Revokes every live family of a person, whatever the app (membership ended, FR-007).</summary>
    public async Task<int> RevokeForUserAsync(Guid userId, string reason, CancellationToken ct)
    {
        var rows = await session.QueryAsync(
            "update publicapi.token_family set revoked_at = now(), revoked_reason = @reason where user_id = @userId and revoked_at is null returning id, app_id",
            new { userId, reason }, r => (Id: r.GetGuid(0), App: r.GetGuid(1)), ct);
        foreach (var row in rows)
        {
            await RecordAsync(row.Id, row.App, userId, reason, ct);
        }
        return rows.Count;
    }

    private async Task<Redemption> RefuseAsync(Guid familyId, CancellationToken ct)
    {
        var row = await session.QuerySingleOrDefaultAsync(
            "select revoked_at is not null or expires_at <= now(), code_redeemed_at is not null from publicapi.token_family where id = @familyId",
            new { familyId }, r => (Ended: r.GetBoolean(0), Redeemed: r.GetBoolean(1)), ct);
        if (row == default || row.Ended)
        {
            return Redemption.Revoked;
        }
        // A live family whose code or generation does not match: a token was used twice.
        await RevokeAsync(familyId, TokenFamilyReasons.ReuseDetected, ct);
        return Redemption.Reused;
    }

    private async Task RecordAsync(Guid familyId, Guid appId, Guid userId, string reason, CancellationToken ct)
    {
        await audit.WriteAsync(new AuditEntry(AuditActions.ApiTokenFamilyRevoked, "api_app", appId.ToString(),
            Changes: new { family = familyId, principal = userId, reason }), ct);
        await outbox.EnqueueAsync(new TokenFamilyRevoked(familyId, appId, userId, reason), ct);
    }
}
