using System.Security.Cryptography;
using System.Text;
using TeamBeats.Audit.Contracts;
using TeamBeats.Entitlements.Contracts;
using TeamBeats.Messaging;
using TeamBeats.Modules.Identity.Contracts;
using TeamBeats.Modules.Integrations.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Primitives;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.Integrations.Microsoft365;

/// <summary>What the employee's Outlook connection looks like for this session. Never carries a token.</summary>
public sealed record OutlookStatus(string State, IReadOnlyList<string> Features, DateTimeOffset? ConnectedAt, DateTimeOffset? ExpiresAt);

/// <summary>The start of "Connect Outlook": Microsoft's page, or the outcome to show when there is nothing to ask for.</summary>
public sealed record OutlookConnectStart(string? Url, string? Binding, string? Outcome);

/// <summary>Where the connection's delegated features stand: which are on, which have admin consent, and the scopes they need.</summary>
internal sealed record DelegatedSetup(Guid ConnectionId, string ExternalTenantId, IReadOnlyList<string> Enabled, IReadOnlyList<string> Granted,
    IReadOnlyList<string> Scopes)
{
    public static readonly string[] DelegatedFeatures = [Microsoft365Features.Mail, Microsoft365Features.Calendar, Microsoft365Features.FreeBusy];

    public static async Task<DelegatedSetup?> LoadAsync(IDbSession session, CancellationToken ct, Guid? connectionId = null)
    {
        var rows = await session.QueryAsync(
            """
            select c.id, c.external_tenant_id, f.feature, f.consent_status = 'granted', f.options::text
            from integrations.connection c join integrations.connection_feature f on f.connection_id = c.id
            where c.kind = @k and c.enabled and c.status <> 'disabled' and f.enabled and f.feature = any(@features)
              and (@id::uuid is null or c.id = @id)
            order by c.created_at, f.feature
            """, new { k = ConnectionKinds.Microsoft365, features = DelegatedFeatures, id = connectionId },
            r => (Id: r.GetGuid(0), External: r.GetString(1), Feature: r.GetString(2), Granted: r.GetBoolean(3), Options: r.GetString(4)), ct);
        if (rows.Count == 0)
        {
            return null;
        }
        // One Microsoft 365 connection serves delegated features in R1: the oldest one with a delegated feature on.
        var first = rows[0];
        var mine = rows.Where(r => r.Id == first.Id).ToList();
        var scopes = mine.Where(r => r.Granted)
            .SelectMany(r => FeatureCatalogue.PermissionsFor(FeatureCatalogue.Find(r.Feature)!, FeatureOptions.FromStored(r.Options)).Select(p => p.Name))
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        return new DelegatedSetup(first.Id, first.External, mine.Select(r => r.Feature).ToList(), mine.Where(r => r.Granted).Select(r => r.Feature).ToList(), scopes);
    }

    /// <summary>The features a token with these scopes can serve.</summary>
    public async Task<IReadOnlyList<string>> CoveredAsync(IDbSession session, IReadOnlyCollection<string> tokenScopes, CancellationToken ct)
    {
        var options = (await session.QueryAsync("select feature, options::text from integrations.connection_feature where connection_id = @c",
            new { c = ConnectionId }, r => (Feature: r.GetString(0), Options: r.GetString(1)), ct)).ToDictionary(r => r.Feature, r => r.Options);
        return Granted.Where(f => FeatureCatalogue.PermissionsFor(FeatureCatalogue.Find(f)!, FeatureOptions.FromStored(options.GetValueOrDefault(f)))
            .All(p => tokenScopes.Contains(p.Name))).ToList();
    }
}

/// <summary>
/// Removes delegated tokens (spec 003 FR-016, spec 018 FR-005, FR-013): on disconnect, sign-out, session revocation,
/// membership disable, and when a feature change leaves a token holding scopes no feature needs any more.
/// </summary>
public sealed class DelegatedTokenStore(IDbSession session, IAuditWriter audit, IOutbox outbox)
{
    public Task<int> RemoveForUserAsync(Guid userId, string reason, CancellationToken ct) =>
        RemoveAsync("delete from integrations.delegated_token where user_id = @u returning user_id", new { u = userId }, reason, ct);

    public Task<int> RemoveForSessionAsync(Guid userId, Guid sessionId, string reason, CancellationToken ct) =>
        RemoveAsync("delete from integrations.delegated_token where user_id = @u and session_id = @s returning user_id",
            new { u = userId, s = sessionId }, reason, ct);

    /// <summary>Drops tokens of a connection that hold a scope no turned-on, consented feature needs (all of them when none is left).</summary>
    public async Task<int> PruneAsync(Guid connectionId, string reason, CancellationToken ct)
    {
        var setup = await DelegatedSetup.LoadAsync(session, ct, connectionId);
        var needed = setup?.Scopes.ToArray() ?? [];
        return await RemoveAsync(
            "delete from integrations.delegated_token where connection_id = @c and not (scopes <@ @needed) returning user_id",
            new { c = connectionId, needed }, reason, ct);
    }

    private async Task<int> RemoveAsync(string sql, object args, string reason, CancellationToken ct)
    {
        var users = (await session.QueryAsync(sql, args, r => r.GetGuid(0), ct)).Distinct().ToList();
        foreach (var user in users)
        {
            await audit.WriteAsync(new AuditEntry(AuditActions.OutlookDisconnected, "user", user.ToString(), Changes: new { reason }), ct);
            await outbox.EnqueueAsync(new OutlookDisconnected(user, reason), ct);
        }
        return users.Count;
    }
}

/// <summary>
/// "Connect Outlook" for the signed-in employee (spec 018 plan "Delegated access", spec 003 FR-016): an authorization
/// code flow with PKCE against the TeamBeats application in the connection's directory, asking only for the scopes of
/// the features that are on and consented. No refresh token in R1; the access token lives for this session only.
/// </summary>
public sealed class DelegatedAccessService(
    IDbSession session,
    TenantContextAccessor context,
    IEntitlementService entitlements,
    IMicrosoftGraph graph,
    TokenProtector protector,
    DelegatedTokenStore tokens,
    IUserDirectory users,
    IAuditWriter audit,
    IOutbox outbox,
    IClock clock)
{
    public static readonly TimeSpan ConnectLifetime = TimeSpan.FromMinutes(10);
    public const string GraphScopePrefix = "https://graph.microsoft.com/";

    public async Task<OutlookStatus> StatusAsync(CancellationToken ct)
    {
        await entitlements.DemandAsync(Capabilities.IntegrationMicrosoft365, ct);
        var (user, sid) = Actor();
        var setup = await DelegatedSetup.LoadAsync(session, ct);
        if (setup is null)
        {
            return new OutlookStatus(M365States.TurnedOff, [], null, null);
        }
        if (setup.Granted.Count == 0)
        {
            return new OutlookStatus(M365States.AdminSetup, [], null, null);
        }
        var token = await session.QuerySingleOrDefaultAsync(
            "select scopes, created_at, expires_at from integrations.delegated_token where user_id = @u and session_id = @s and connection_id = @c",
            new { u = user, s = sid, c = setup.ConnectionId },
            r => (Scopes: r.GetFieldValue<string[]>(0), Created: r.Get<DateTimeOffset>("created_at"), Expires: r.Get<DateTimeOffset>("expires_at")), ct);
        if (token == default)
        {
            var refused = await session.QuerySingleOrDefaultAsync(
                """
                select outcome from integrations.outlook_connect_request
                where user_id = @u and session_id = @s and connection_id = @c and consumed_at is not null
                order by consumed_at desc limit 1
                """, new { u = user, s = sid, c = setup.ConnectionId }, r => r.Get<string>("outcome"), ct);
            var state = refused is M365States.AdminSetup or M365States.ConditionalAccess ? refused : M365States.NotConnected;
            return new OutlookStatus(protector.Configured ? state : M365States.Degraded, [], null, null);
        }
        var covered = await setup.CoveredAsync(session, token.Scopes, ct);
        // A feature turned on after connecting needs its scope added: the shell repeats the silent connect.
        var current = token.Expires > clock.UtcNow && setup.Scopes.All(token.Scopes.Contains);
        return new OutlookStatus(current ? M365States.Ok : M365States.Reconnect, covered, token.Created, token.Expires);
    }

    /// <summary>Starts the connect. <paramref name="tenantKey"/> travels in the state so the platform callback can route back.</summary>
    public async Task<OutlookConnectStart> StartConnectAsync(string tenantKey, string redirectUri, bool silent, string returnTo, CancellationToken ct)
    {
        await entitlements.DemandAsync(Capabilities.IntegrationMicrosoft365, ct);
        var (user, sid) = Actor();
        var setup = await DelegatedSetup.LoadAsync(session, ct);
        if (setup is null)
        {
            return new OutlookConnectStart(null, null, M365States.TurnedOff);
        }
        if (setup.Scopes.Count == 0)
        {
            return new OutlookConnectStart(null, null, M365States.AdminSetup);
        }
        if (!protector.Configured)
        {
            return new OutlookConnectStart(null, null, M365States.Degraded);
        }

        var state = tenantKey + "." + Random();
        var binding = Random();
        var verifier = Random();
        var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        var now = clock.UtcNow;
        await session.ExecAsync("delete from integrations.outlook_connect_request where expires_at < @cutoff", new { cutoff = now - TimeSpan.FromHours(1) }, ct);
        await session.ExecAsync("delete from integrations.delegated_token where expires_at < @cutoff", new { cutoff = now - TimeSpan.FromHours(1) }, ct);
        await session.ExecAsync(
            """
            insert into integrations.outlook_connect_request
              (tenant_id, id, user_id, session_id, connection_id, state_hash, browser_binding_hash, code_verifier, scopes, return_to, expires_at)
            values (@t, @id, @u, @s, @c, @state, @binding, @verifier, @scopes, @returnTo, @exp)
            """, new
            {
                t = context.RequireTenant(), id = Ids.New(), u = user, s = sid, c = setup.ConnectionId, state = Hash(state), binding = Hash(binding),
                verifier, scopes = setup.Scopes.ToArray(), returnTo, exp = now + ConnectLifetime,
            }, ct);
        var hint = (await users.GetAsync(user, ct))?.Email;
        var url = graph.AuthorizeUrl(setup.ExternalTenantId, setup.Scopes.Select(s => GraphScopePrefix + s), redirectUri, state, challenge, hint, silent);
        return new OutlookConnectStart(url, binding, null);
    }

    /// <summary>
    /// Completes the connect on the tenant host, as the same user and session that started it. Returns where to send the
    /// browser and the outcome (<c>connected</c> or an <see cref="M365States"/> value, or <c>failed</c>).
    /// </summary>
    public async Task<(string ReturnTo, string Outcome)> CompleteConnectAsync(string? state, string? binding, string? code, string? error,
        string? errorDescription, string redirectUri, CancellationToken ct)
    {
        await entitlements.DemandAsync(Capabilities.IntegrationMicrosoft365, ct);
        var (user, sid) = Actor();
        if (string.IsNullOrEmpty(state))
        {
            return ("/", "failed");
        }
        var pending = await session.QuerySingleOrDefaultAsync(
            """
            update integrations.outlook_connect_request set consumed_at = now()
            where state_hash = @h and consumed_at is null and expires_at > @now
            returning id, user_id, session_id, connection_id, browser_binding_hash, code_verifier, scopes, return_to
            """, new { h = Hash(state), now = clock.UtcNow },
            r => (Id: r.GetGuid(0), User: r.GetGuid(1), Session: r.GetGuid(2), Connection: r.GetGuid(3), Binding: r.GetString(4), Verifier: r.GetString(5),
                Scopes: r.GetFieldValue<string[]>(6), ReturnTo: r.GetString(7)), ct);
        if (pending == default)
        {
            return ("/", "failed");
        }
        await session.ExecAsync("update integrations.outlook_connect_request set code_verifier = '' where id = @id", new { id = pending.Id }, ct);
        if (pending.User != user || pending.Session != sid || string.IsNullOrEmpty(binding)
            || !CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(Hash(binding)), Encoding.ASCII.GetBytes(pending.Binding)))
        {
            return ("/", "failed");
        }

        var outcome = await RedeemAsync(pending.Connection, pending.Scopes, pending.Verifier, code, error, errorDescription, redirectUri, user, sid, ct);
        if (outcome != "connected")
        {
            await session.ExecAsync("update integrations.outlook_connect_request set outcome = @o where id = @id", new { id = pending.Id, o = outcome }, ct);
        }
        return (pending.ReturnTo, outcome);
    }

    private async Task<string> RedeemAsync(Guid connectionId, string[] requested, string verifier, string? code, string? error, string? errorDescription,
        string redirectUri, Guid user, Guid sid, CancellationToken ct)
    {
        if (!string.IsNullOrEmpty(error))
        {
            return MapRefusal(error, errorDescription);
        }
        var setup = await DelegatedSetup.LoadAsync(session, ct, connectionId);
        if (setup is null || string.IsNullOrEmpty(code))
        {
            return "failed";
        }
        DelegatedGrant grant;
        try
        {
            grant = await graph.RedeemDelegatedCodeAsync(setup.ExternalTenantId, code, redirectUri, verifier, requested.Select(s => GraphScopePrefix + s), ct);
        }
        catch (DelegatedAccessRefusedException refused)
        {
            return MapRefusal(refused.Reason, null);
        }
        catch (GraphUnavailableException)
        {
            return M365States.Degraded;
        }

        // Keep only what was asked for and granted, as plain Graph permission names.
        var granted = grant.Scopes.Select(s => s.StartsWith(GraphScopePrefix, StringComparison.OrdinalIgnoreCase) ? s[GraphScopePrefix.Length..] : s)
            .Where(s => requested.Contains(s, StringComparer.Ordinal)).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        var tenantId = context.RequireTenant();
        var (nonce, ciphertext) = protector.Protect(grant.AccessToken, tenantId, user, sid);
        await session.ExecAsync(
            """
            insert into integrations.delegated_token (tenant_id, user_id, session_id, connection_id, scopes, nonce, ciphertext, expires_at)
            values (@t, @u, @s, @c, @scopes, @nonce, @ciphertext, @exp)
            on conflict (tenant_id, user_id, session_id) do update
              set connection_id = @c, scopes = @scopes, nonce = @nonce, ciphertext = @ciphertext, expires_at = @exp, created_at = now()
            """, new { t = tenantId, u = user, s = sid, c = connectionId, scopes = granted, nonce, ciphertext, exp = grant.ExpiresAt }, ct);
        var features = await setup.CoveredAsync(session, granted, ct);
        await audit.WriteAsync(new AuditEntry(AuditActions.OutlookConnected, "user", user.ToString(), Changes: new { features, scopes = granted }), ct);
        await outbox.EnqueueAsync(new OutlookConnected(user, features), ct);
        return "connected";
    }

    /// <summary>Employee disconnect (spec 018 FR-013): deletes every delegated token of this membership.</summary>
    public async Task DisconnectAsync(CancellationToken ct)
    {
        await entitlements.DemandAsync(Capabilities.IntegrationMicrosoft365, ct);
        var (user, _) = Actor();
        if (await tokens.RemoveForUserAsync(user, "disconnect", ct) == 0)
        {
            // Audited even when nothing was held, so the employee's action is on record.
            await audit.WriteAsync(new AuditEntry(AuditActions.OutlookDisconnected, "user", user.ToString(), Changes: new { reason = "disconnect" }), ct);
        }
    }

    /// <summary>
    /// Microsoft's refusal codes to states. Missing consent shows admin setup, never another consent prompt (FR-004);
    /// conditional access and MFA blocks show an explanation (FR-012); anything else needs the employee to connect again.
    /// </summary>
    public static string MapRefusal(string error, string? description)
    {
        var text = description ?? string.Empty;
        if (error == "consent_required" || text.Contains("AADSTS65001", StringComparison.Ordinal))
        {
            return M365States.AdminSetup;
        }
        if (text.Contains("AADSTS53", StringComparison.Ordinal) || text.Contains("AADSTS50076", StringComparison.Ordinal)
            || text.Contains("AADSTS50079", StringComparison.Ordinal) || (error == "interaction_required" && description is null))
        {
            return M365States.ConditionalAccess;
        }
        return M365States.NotConnected;
    }

    private (Guid User, Guid Session) Actor() =>
        context is { ActorType: ActorType.User, ActorId: { } u, SessionId: { } s } ? (u, s) : throw AppException.Unauthenticated();

    private static string Random() => Base64Url(RandomNumberGenerator.GetBytes(32));

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}

public sealed class DropTokensOnSessionEnded(DelegatedTokenStore tokens) : IMessageHandler<SessionEnded>
{
    public InactiveTenantPolicy InactiveTenantPolicy => InactiveTenantPolicy.Process;

    public Task HandleAsync(SessionEnded message, CancellationToken ct) => tokens.RemoveForSessionAsync(message.UserId, message.SessionId, "sign_out", ct);
}

public sealed class DropTokensOnSessionRevoked(DelegatedTokenStore tokens) : IMessageHandler<SessionRevoked>
{
    public InactiveTenantPolicy InactiveTenantPolicy => InactiveTenantPolicy.Process;

    public Task HandleAsync(SessionRevoked message, CancellationToken ct) => tokens.RemoveForUserAsync(message.UserId, "session_revoked", ct);
}

public sealed class DropTokensOnUserDeprovisioned(DelegatedTokenStore tokens) : IMessageHandler<UserDeprovisioned>
{
    public InactiveTenantPolicy InactiveTenantPolicy => InactiveTenantPolicy.Process;

    public Task HandleAsync(UserDeprovisioned message, CancellationToken ct) => tokens.RemoveForUserAsync(message.UserId, "membership_disabled", ct);
}
