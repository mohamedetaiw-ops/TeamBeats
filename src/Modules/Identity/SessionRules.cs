using Microsoft.Extensions.DependencyInjection;
using TeamBeats.Modules.Security.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Primitives;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.Identity;

/// <summary>
/// Tenant session rules (spec 026 US2 scenario 4, FR-003): a session ends when it has been idle longer than the idle
/// timeout or is older than the absolute timeout. Use is recorded at most once a minute. These writes run on their own
/// short database session so they hold even when the request itself changes nothing or fails.
/// </summary>
public static class SessionRules
{
    public static readonly TimeSpan TouchEvery = TimeSpan.FromMinutes(1);

    public const string IdleTimeout = "idle_timeout";
    public const string AbsoluteTimeout = "absolute_timeout";
    public const string SessionLimit = "session_limit";
    public const string SignOutEverywhere = "sign_out_everywhere";
    public const string AdminRevoked = "admin_revoked";

    /// <summary>Why the session has ended under the policy, or null while it is still valid.</summary>
    public static string? Ended(SecurityPolicySnapshot policy, DateTimeOffset created, DateTimeOffset lastSeen, DateTimeOffset now)
    {
        if (now - created > TimeSpan.FromHours(policy.AbsoluteHours))
        {
            return AbsoluteTimeout;
        }
        if (now - lastSeen > TimeSpan.FromMinutes(policy.IdleMinutes))
        {
            return IdleTimeout;
        }
        return null;
    }

    public static async Task TouchAsync(IServiceProvider services, Guid sessionId, DateTimeOffset lastSeen, CancellationToken ct, TimeSpan? every = null)
    {
        if (services.GetRequiredService<IClock>().UtcNow - lastSeen < (every ?? TouchEvery))
        {
            return;
        }
        await OnOwnSessionAsync(services, "update identity.user_session set last_seen_at = now() where id = @id", new { id = sessionId }, ct);
    }

    public static Task EndAsync(IServiceProvider services, Guid sessionId, string reason, CancellationToken ct) =>
        OnOwnSessionAsync(services, "update identity.user_session set revoked_at = now(), revoked_reason = @r where id = @id and revoked_at is null",
            new { id = sessionId, r = reason }, ct);

    private static async Task OnOwnSessionAsync(IServiceProvider services, string sql, object args, CancellationToken ct)
    {
        await using var own = new DbSession(services.GetRequiredService<AppDataSource>(), services.GetRequiredService<TenantContextAccessor>());
        await own.ExecAsync(sql, args, ct);
        await own.CommitAsync(ct);
    }
}
