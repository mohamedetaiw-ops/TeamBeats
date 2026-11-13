using TeamBeats.Modules.Policies.Contracts;
using TeamBeats.Primitives;

namespace TeamBeats.Modules.Policies;

/// <summary>Policies other modules link to (spec 026 US7). A policy the caller may not see, or a plan without policies, gives null.</summary>
public sealed class PolicyReferences(PolicyRegister register) : IPolicyReferences
{
    public async Task<PolicyReference?> VisibleAsync(Guid policyId, CancellationToken ct = default)
    {
        try
        {
            var policy = await register.VisibleAsync(policyId, false, ct);
            return new PolicyReference(policy.Id, policy.Code, policy.Names);
        }
        catch (AppException e) when (e.Status is 403 or 404)
        {
            return null;
        }
    }
}
