using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using TeamBeats.Entitlements.Contracts;
using TeamBeats.Localization;
using TeamBeats.Messaging;
using TeamBeats.Modules.Identity.Contracts;
using TeamBeats.Modules.Integrations.Contracts;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Primitives;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.Integrations.Microsoft365;

/// <summary>
/// The one address Bot Framework posts every Teams activity to, on the platform host. A call counts only with a valid
/// Bot Framework token. A quick action names its tenant in its own button token (spec 015 FR-018). A chat message
/// (spec 014 US8) names only the person's Microsoft 365 directory: the bot looks in each tenant that reaches Teams
/// through that directory for the person linked under their Entra object ID, and hands the message to the assistant of
/// the one tenant that has them. Nobody, or several tenants, gets a short answer and nothing else.
/// </summary>
public static class TeamsBotEndpoint
{
    public const string Path = "/api/v1/integrations/teams/bot";

    /// <summary>Bot Framework calls this for every tenant, so the limit per client address is generous.</summary>
    public const string RateLimit = "integrations.teams_bot";

    private const int MaxBody = 64 * 1024;

    public static IEndpointRouteBuilder MapTeamsBot(this IEndpointRouteBuilder app)
    {
        app.MapPost(Path, async (HttpRequest request, ITenantContext context, TenantContextAccessor accessor, ITeamsBot bot, ITeamsCardActions actions,
            TeamsChatRouter chats, ITenantRegistry tenants, IDbSession session, CancellationToken ct) =>
        {
            if (context.Host.Kind != HostScopeKind.Platform)
            {
                throw AppException.NotFound();
            }
            string body;
            using (var reader = new StreamReader(request.Body))
            {
                var buffer = new char[MaxBody + 1];
                var read = await reader.ReadBlockAsync(buffer, ct);
                if (read > MaxBody)
                {
                    return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
                }
                body = new string(buffer, 0, read);
            }
            var call = await bot.ReadAsync(request.Headers.Authorization.ToString() is { Length: > 0 } auth ? auth : null, body, ct);
            if (!call.Authentic)
            {
                return Results.Unauthorized();
            }
            if (call.Message is { } message)
            {
                await chats.RouteAsync(message, ct);
                return Results.Ok();
            }
            if (call.Action is not { } pressed)
            {
                return Results.Ok();
            }
            if (actions.TenantOf(pressed.Token) is not { } tenantId || await tenants.FindByIdAsync(tenantId, ct) is not { Status: TenantLifecycleStatus.Active })
            {
                return Results.Ok();
            }
            accessor.SetTenant(tenantId);
            accessor.SetActor(ActorType.System, null);
            var reply = await bot.ReplyAsync(await actions.RunAsync(pressed, ct), ct);
            await session.CommitAsync(ct);
            return Results.Ok(reply);
        }).RequireRateLimiting(RateLimit).WithTags("Integrations: Teams bot");
        return app;
    }
}

/// <summary>Finds the tenant and person a Teams chat message comes from (spec 014 US8) and queues it for the assistant.</summary>
public sealed class TeamsChatRouter(
    IDbSession session,
    TenantContextAccessor accessor,
    ITenantRegistry tenants,
    IProvisioningTarget identities,
    IDirectorySource directory,
    ITenantMemberships memberships,
    IEntitlementService entitlements,
    IOutbox outbox,
    BotFramework bot,
    ILocalizer localizer)
{
    public const int MaxText = 2000;

    public async Task RouteAsync(TeamsBotMessage message, CancellationToken ct)
    {
        var locale = Locales.IsSupported(message.Locale?.Split('-')[0] ?? "") ? message.Locale!.Split('-')[0] : Locales.Fallback;
        if (!Guid.TryParse(message.EntraTenantId, out var directoryId))
        {
            return;
        }
        var external = directoryId.ToString("D");
        var candidates = await session.QueryAsync(
            "select tenant_id from integrations.teams_route where external_tenant_id = @d", new { d = external }, r => r.GetGuid(0), ct);
        var found = new List<(Guid Tenant, Guid User)>();
        foreach (var tenant in candidates.Distinct())
        {
            if (await tenants.FindByIdAsync(tenant, ct) is not { Status: TenantLifecycleStatus.Active })
            {
                continue;
            }
            accessor.SetTenant(tenant);
            accessor.SetActor(ActorType.System, null);
            if (await identities.FindUserAsync(directory.IssuerFor(external), message.AadObjectId, ct) is { } user
                && await memberships.GetAsync(user, ct) is { Status: MembershipStatus.Active })
            {
                found.Add((tenant, user));
            }
        }
        if (found.Count != 1)
        {
            accessor.SetTenant(null);
            await AnswerAsync(message, found.Count == 0 ? "integrations.teams.chat.no_account" : "integrations.teams.chat.several", locale, ct);
            return;
        }
        var (chosen, person) = found[0];
        accessor.SetTenant(chosen);
        if (!await entitlements.IsEnabledAsync(Capabilities.AiAssistantTeams, ct))
        {
            await AnswerAsync(message, "integrations.teams.chat.unavailable", locale, ct);
            return;
        }
        var text = message.Text.Length > MaxText ? message.Text[..MaxText] : message.Text;
        await outbox.EnqueueAsync(new TeamsChatReceived(person, text, locale, message.Conversation.ServiceUrl, message.Conversation.ConversationId), ct);
        await session.CommitAsync(ct);
    }

    /// <summary>A short answer from the bot itself, when no assistant can take the message.</summary>
    private Task AnswerAsync(TeamsBotMessage message, string key, string locale, CancellationToken ct) =>
        bot.ReplyAsync(message.Conversation, BotFramework.ReplyCard(new TeamsCardReply(localizer.Get(key, locale), null, null, Locales.IsRightToLeft(locale)), null,
            Locales.IsRightToLeft(locale)), localizer.Get(key, locale), ct);
}
