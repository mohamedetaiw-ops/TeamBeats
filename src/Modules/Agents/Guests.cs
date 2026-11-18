using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Primitives;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.Agents;

/// <summary>
/// Guests and the assistant (spec 014 AST-022): a guest may use the assistant only when the tenant has turned it on in
/// the assistant settings. When it is on, nothing else changes, because retrieval already runs as the guest and owners
/// show a guest only the spaces they were invited to. A guest can always delete their own conversations.
/// </summary>
public sealed class AssistantGuests(IDbSession session, ITenantContext context, ITenantMemberships memberships)
{
    private bool? _allowed;

    /// <summary>True when the caller is not a guest, or is a guest and the tenant lets guests use the assistant.</summary>
    public async Task<bool> AllowedAsync(CancellationToken ct)
    {
        if (_allowed is { } known)
        {
            return known;
        }
        var user = context.RequireUser();
        var guest = await memberships.GetAsync(user, ct) is { MembershipType: MembershipTypes.Guest };
        _allowed = !guest || await session.ScalarAsync<bool?>("select guests_allowed from agents.assistant_settings", null, ct) == true;
        return _allowed.Value;
    }

    public async Task DemandAsync(CancellationToken ct)
    {
        if (!await AllowedAsync(ct))
        {
            throw AppException.Denied();
        }
    }
}
