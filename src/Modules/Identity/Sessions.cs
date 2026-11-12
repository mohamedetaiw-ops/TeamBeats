using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http;
using TeamBeats.Audit.Contracts;
using TeamBeats.Messaging;
using TeamBeats.Modules.Identity.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Primitives;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.Identity;

public sealed class SessionOptions
{
    public const string Section = "Auth:Sessions";

    public TimeSpan Lifetime { get; set; } = TimeSpan.FromHours(8);
    public string CookieName { get; set; } = "tb_session";
}

public static class SessionTokens
{
    public static string NewToken() => Base64Url(RandomNumberGenerator.GetBytes(32));

    public static string Hash(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

/// <summary>Creates and revokes server-side sessions (ADR-0007). The browser only ever holds an opaque token.</summary>
public sealed class SessionService(IDbSession session, ITenantContext context, IAuditWriter audit, IOutbox outbox, IClock clock,
    Microsoft.Extensions.Options.IOptions<SessionOptions> options, TeamBeats.Modules.Security.Contracts.ISecurityPolicy policies) : ISessionRevocation
{
    /// <param name="kioskDeviceId">Set for a kiosk session (spec 030 US4): it lives only while that device stays enrolled.</param>
    public async Task<string> CreateUserSessionAsync(Guid userId, string method, CancellationToken ct, Guid? kioskDeviceId = null)
    {
        var token = SessionTokens.NewToken();
        await session.ExecAsync(
            """
            insert into identity.user_session (tenant_id, id, user_id, token_hash, method, expires_at, source_ip, authenticated_at, last_seen_at, kiosk_device_id)
            values (@t, @id, @u, @h, @m, @exp, @ip::inet, @now, @now, @kiosk)
            """,
            new
            {
                t = context.RequireTenant(), id = Ids.New(), u = userId, h = SessionTokens.Hash(token), m = method, exp = clock.UtcNow + options.Value.Lifetime,
                ip = System.Net.IPAddress.TryParse(context.SourceIp, out var address) ? address.ToString() : null, now = clock.UtcNow, kiosk = kioskDeviceId,
            }, ct);

        // Concurrent session limit (spec 026 FR-003): the oldest sessions beyond the limit end on a new sign-in.
        if ((await policies.GetAsync(ct)).MaxSessions is { } max)
        {
            await session.ExecAsync(
                """
                update identity.user_session set revoked_at = now(), revoked_reason = @r
                where id in (select id from identity.user_session where user_id = @u and revoked_at is null and expires_at > now()
                             order by created_at desc, id desc offset @max)
                """, new { u = userId, max, r = SessionRules.SessionLimit }, ct);
        }
        return token;
    }

    public async Task<string> CreatePlatformSessionAsync(Guid operatorId, CancellationToken ct)
    {
        var token = SessionTokens.NewToken();
        await session.ExecAsync(
            "insert into identity.platform_session (id, operator_id, token_hash, expires_at) values (@id, @o, @h, @exp)",
            new { id = Ids.New(), o = operatorId, h = SessionTokens.Hash(token), exp = clock.UtcNow + options.Value.Lifetime }, ct);
        return token;
    }

    /// <param name="persistent">False for a kiosk session: the cookie goes when the browser closes (spec 030 FR-016).</param>
    public void SetCookie(HttpResponse response, string token, bool persistent = true) =>
        response.Cookies.Append(options.Value.CookieName, token, new CookieOptions
        {
            HttpOnly = true,
            Secure = true,
            SameSite = SameSiteMode.Lax,
            Path = "/",
            MaxAge = persistent ? options.Value.Lifetime : null,
        });

    public void ClearCookie(HttpResponse response) =>
        response.Cookies.Delete(options.Value.CookieName, new CookieOptions { HttpOnly = true, Secure = true, SameSite = SameSiteMode.Lax, Path = "/" });

    public async Task SignOutAsync(CancellationToken ct)
    {
        if (context.SessionId is not { } sid)
        {
            return;
        }
        if (context.ActorType == ActorType.PlatformOperator)
        {
            await session.ExecAsync("update identity.platform_session set revoked_at = now() where id = @id", new { id = sid }, ct);
            return;
        }

        await session.ExecAsync("update identity.user_session set revoked_at = now(), revoked_reason = 'sign_out' where id = @id and revoked_at is null",
            new { id = sid }, ct);
        await audit.WriteAsync(new AuditEntry(AuditActions.SessionRevoked, "user", context.ActorId.ToString(), Changes: new { reason = "sign_out", count = 1 }), ct);
        if (context.ActorId is { } userId)
        {
            await outbox.EnqueueAsync(new SessionEnded(userId, sid), ct);
        }
    }

    public async Task<int> RevokeUserSessionsAsync(Guid userId, string reason, CancellationToken ct = default)
    {
        var count = await session.ExecAsync(
            "update identity.user_session set revoked_at = now(), revoked_reason = @r where user_id = @u and revoked_at is null",
            new { u = userId, r = reason }, ct);
        await audit.WriteAsync(new AuditEntry(AuditActions.SessionRevoked, "user", userId.ToString(), Changes: new { reason, count }), ct);
        await outbox.EnqueueAsync(new SessionRevoked(userId, count, reason), ct);
        return count;
    }

    public async Task<int> RevokeTenantSessionsAsync(string reason, CancellationToken ct = default)
    {
        var count = await session.ExecAsync(
            "update identity.user_session set revoked_at = now(), revoked_reason = @r where revoked_at is null",
            new { r = reason }, ct);
        await audit.WriteAsync(new AuditEntry(AuditActions.SessionRevoked, "tenant", context.TenantId.ToString(), Changes: new { reason, count }), ct);
        return count;
    }
}
