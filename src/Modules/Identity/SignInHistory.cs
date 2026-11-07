using TeamBeats.Modules.Identity.Contracts;
using TeamBeats.Persistence;

namespace TeamBeats.Modules.Identity;

/// <summary>Session source addresses for the spec 026 lock-out preview, read from this module's own session table.</summary>
public sealed class SignInHistory(IDbSession session) : ISignInHistory
{
    public async Task<IReadOnlyList<SignInAddress>> AddressesSinceAsync(DateTimeOffset since, CancellationToken ct = default) =>
        await session.QueryAsync(
            "select distinct user_id, host(source_ip) from identity.user_session where created_at >= @since and source_ip is not null",
            new { since }, r => new SignInAddress(r.GetGuid(0), r.GetString(1)), ct);
}
