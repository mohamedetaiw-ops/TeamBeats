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
    Microsoft.Extensions.Options.IOptions<SessionOptions> options) : ISessionRevocation
{
    public async Task<string> CreateUserSessionAsync(Guid userId, string method, CancellationToken ct)
    {
        var token = SessionTokens.NewToken();
        await session.ExecAsync(
            """
            insert into identity.user_session (tenant_id, id, user_id, token_hash, method, expires_at)
            values (@t, @id, @u, @h, @m, @exp)
            """,
            new { t = context.RequireTenant(), id = Ids.New(), u = userId, h = SessionTokens.Hash(token), m = method, exp = clock.UtcNow + options.Value.Lifetime }, ct);
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

    public void SetCookie(HttpResponse response, string token) =>
        response.Cookies.Append(options.Value.CookieName, token, new CookieOptions
        {
            HttpOnly = true,
            Secure = true,
            SameSite = SameSiteMode.Lax,
            Path = "/",
            MaxAge = options.Value.Lifetime,
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
