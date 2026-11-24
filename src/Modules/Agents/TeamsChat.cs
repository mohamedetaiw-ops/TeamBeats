using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TeamBeats.Localization;
using TeamBeats.Messaging;
using TeamBeats.Modules.Agents.Contracts;
using TeamBeats.Modules.Identity.Contracts;
using TeamBeats.Modules.Integrations.Contracts;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Primitives;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.Agents;

/// <summary>
/// The assistant in Teams (spec 014 US8, T016). A chat message to the TeamBeats bot is answered by the knowledge agent
/// as the person, with their permissions at that moment, in a scope of its own. Teams is a chat outside TeamBeats, so
/// the answer draws on internal content at most; anything more sensitive, and any step that needs a confirmation, is
/// finished in TeamBeats. A message within 12 hours of the last one continues the same conversation, which shows in the
/// web history like any other. The reply is a card with the answer, the cited sources and a link to continue.
/// </summary>
public sealed class TeamsAssistantChat(IServiceScopeFactory scopes, ITenantContext context, IClock clock, ILogger<TeamsAssistantChat> logger)
    : IMessageHandler<TeamsChatReceived>
{
    public static readonly TimeSpan Continuation = TimeSpan.FromHours(12);

    /// <summary>The highest classification an answer in Teams may draw on.</summary>
    public const string MaxClassification = "internal";

    public const int MaxLinks = 5;

    public InactiveTenantPolicy InactiveTenantPolicy => InactiveTenantPolicy.Drop;

    public async Task HandleAsync(TeamsChatReceived message, CancellationToken ct)
    {
        var tenant = context.RequireTenant();
        await using var scope = scopes.CreateAsyncScope();
        var sp = scope.ServiceProvider;
        var accessor = sp.GetRequiredService<TenantContextAccessor>();
        accessor.SetTenant(tenant);
        accessor.SetActor(ActorType.System, null);
        if (await sp.GetRequiredService<ITenantMemberships>().GetAsync(message.UserId, ct) is not { Status: MembershipStatus.Active })
        {
            return;
        }
        accessor.SetActor(ActorType.User, message.UserId);
        var session = sp.GetRequiredService<IDbSession>();
        var localizer = sp.GetRequiredService<ILocalizer>();
        var now = clock.UtcNow;
        var conversation = (await session.QueryAsync(
            """
            select t.conversation_id from agents.teams_chat t
            join agents.conversation c on c.tenant_id = t.tenant_id and c.id = t.conversation_id and c.user_id = t.user_id
            where t.user_id = @user and t.updated_at > @since
            """, new { user = message.UserId, since = now - Continuation }, r => r.GetGuid(0), ct)).Cast<Guid?>().FirstOrDefault();

        TurnView? turn = null;
        try
        {
            turn = await sp.GetRequiredService<AssistantService>().AskAsync(
                new TurnRequest(conversation, message.Text, MaxClassification: MaxClassification), ct);
        }
        catch (AppException ex)
        {
            logger.LogInformation("The assistant did not take a Teams message ({Status})", ex.Status);
        }

        var locale = message.Locale;
        var rtl = Locales.IsRightToLeft(locale);
        var text = turn switch
        {
            { Outcome: AssistantOutcomes.Answered, Answer: { Length: > 0 } answer } => answer,
            { Outcome: AssistantOutcomes.ActionPending } => localizer.Get("agents.teams.more", locale),
            _ => localizer.Get("agents.teams.no_answer", locale),
        };
        var links = turn is { Outcome: AssistantOutcomes.Answered }
            ? turn.Citations.Where(c => c.Anchor.StartsWith('/')).DistinctBy(c => c.Anchor).Take(MaxLinks)
                .Select(c => new TeamsCardLink(c.Title, c.Anchor)).ToList()
            : [];
        var continueAt = turn is null ? "/assistant" : $"/assistant?c={turn.ConversationId:D}";
        if (turn is not null)
        {
            await session.ExecAsync(
                """
                insert into agents.teams_chat (tenant_id, user_id, conversation_id, updated_at)
                values (platform.current_tenant_id(), @user, @conversation, @now)
                on conflict (tenant_id, user_id) do update set conversation_id = excluded.conversation_id, updated_at = excluded.updated_at
                """, new { user = message.UserId, conversation = turn.ConversationId, now }, ct);
        }
        var card = new TeamsCard(localizer.Get("agents.teams.title", locale), text, continueAt, localizer.Get("agents.teams.continue", locale), [], null, rtl, links);
        var outcome = await sp.GetRequiredService<ITeamsMessaging>().ReplyAsync(message.UserId, new TeamsConversation(message.ServiceUrl, message.ConversationId), card, ct);
        if (outcome == TeamsSendOutcome.Transient)
        {
            // Nothing is kept, so the message is answered again on its next delivery.
            throw new InvalidOperationException("The Teams reply could not be sent yet.");
        }
        await session.CommitAsync(ct);
    }
}

/// <summary>A person's Teams chat link ends when they leave the tenant (spec 003).</summary>
public sealed class TeamsChatLeavers(IDbSession session) : IMessageHandler<UserDeprovisioned>
{
    public Task HandleAsync(UserDeprovisioned message, CancellationToken ct) =>
        session.ExecAsync("delete from agents.teams_chat where user_id = @user", new { user = message.UserId }, ct);
}
