using System.Text.Json;
using TeamBeats.Modules.Agents.Contracts;
using TeamBeats.Modules.Identity.Contracts;
using TeamBeats.Modules.Tenancy.Contracts;

namespace TeamBeats.Modules.PublicApi;

/// <summary>
/// <c>whoami</c> (spec 035 section 6, scope <c>profile:read</c>), exposable to external AI clients: the employee the
/// client acts for, as the public <c>/me</c> operation shows them: name, email, membership type and language.
/// </summary>
public sealed class WhoAmITool(IUserDirectory users, ITenantMemberships memberships) : IAgentTool
{
    public const string Name = "whoami";

    public ToolDefinition Definition { get; } = new(
        Name, "publicapi", 1, ToolSideEffects.Read, ToolApprovalRules.Execute,
        ToolArgs.Schema("""{"type":"object","additionalProperties":false,"properties":{}}"""),
        "internal", Idempotent: true, TimeoutSeconds: 5,
        "Tell who the signed-in TeamBeats employee is: name, email, membership type and preferred language.",
        Exposable: true);

    public async Task<ToolResult> ExecuteAsync(JsonElement arguments, ToolCallContext context, CancellationToken ct)
    {
        var user = await users.GetAsync(context.UserId, ct);
        var membership = await memberships.GetAsync(context.UserId, ct);
        if (user is null || membership is null)
        {
            return ToolResult.Failed("tool.denied");
        }
        var content = ToolArgs.Json(new { id = context.UserId, name = user.DisplayName, email = user.Email, membershipType = membership.MembershipType,
            locale = user.PreferredLocale });
        return new ToolResult(true, content, "internal", [new ToolSource("people", context.UserId, user.DisplayName, $"/people/{context.UserId}", "internal")]);
    }
}
