using Microsoft.Extensions.Logging;
using TeamBeats.Localization;
using TeamBeats.Messaging;
using TeamBeats.Modules.Identity.Contracts;
using TeamBeats.Modules.Integrations.Contracts;
using TeamBeats.Modules.Notifications.Contracts;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Primitives;
using TeamBeats.TenantContext;
using System.Text.Json;

namespace TeamBeats.Modules.Notifications;

/// <summary>
/// Teams activity notifications for the current tenant (spec 015 US5, FR-019). Each due delivery is written from its
/// template in the person's language and sent through the Microsoft 365 connection. Technical failures retry with the
/// email backoff for up to 2 hours. When Teams cannot reach the person (the app is not installed for them, they have no
/// Microsoft 365 account, or the connection or feature is off), the delivery is dropped with that reason and the message
/// goes by email unless email already has it (US5 scenario 3).
/// </summary>
public sealed class TeamsDelivery(
    IDbSession session,
    ITenantContext context,
    ITeamsMessaging teams,
    ITenantMemberships memberships,
    IUserDirectory users,
    ITenantSettingsReader tenantSettings,
    NotificationTemplates templates,
    ILocalizer localizer,
    IOutbox outbox,
    QuickActions quickActions,
    ILogger<TeamsDelivery> logger)
{
    public static readonly TimeSpan RetryFor = TimeSpan.FromHours(2);

    private sealed record Due(Guid Id, Guid NotificationId, Guid UserId, int Attempts, DateTimeOffset CreatedAt, bool Mandatory, string Template,
        Dictionary<string, string> Parameters, string Link, string? Actions);

    public async Task<int> DeliverDueAsync(DateTimeOffset now, int batch, CancellationToken ct)
    {
        var due = await session.QueryAsync(
            """
            select d.id, d.notification_id, d.user_id, d.attempts, d.created_at, n.mandatory, n.template_key, n.parameters::text, n.link, n.actions::text
            from notifications.delivery d
            join notifications.notification n on n.tenant_id = d.tenant_id and n.id = d.notification_id
            where d.status = 'pending' and d.channel = 'teams' and d.not_before <= @now
            order by d.not_before
            limit @batch
            for update of d skip locked
            """,
            new { now, batch },
            r => new Due(r.GetGuid(0), r.GetGuid(1), r.GetGuid(2), r.GetInt32(3), r.GetFieldValue<DateTimeOffset>(4), r.GetBoolean(5), r.GetString(6),
                JsonSerializer.Deserialize<Dictionary<string, string>>(r.GetString(7), JsonDefaults.Options) ?? [], r.GetString(8),
                r.IsDBNull(9) ? null : r.GetString(9)),
            ct);
        if (due.Count == 0)
        {
            return 0;
        }
        var tenantId = context.RequireTenant();
        var tenantLocale = await tenantSettings.GetLocaleSettingsAsync(ct);
        foreach (var d in due)
        {
            if (await memberships.GetAsync(d.UserId, ct) is not { Status: MembershipStatus.Active })
            {
                await FinishAsync(d.Id, "dropped", "member_inactive", now, ct);
                continue;
            }
            var user = await users.GetAsync(d.UserId, ct);
            var locale = LocaleResolver.Resolve(user?.PreferredLocale, tenantLocale.EnabledLocales, tenantLocale.DefaultLocale);
            var rendered = await templates.RenderAsync(d.Template, locale, d.Parameters, ct);
            var label = d.Mandatory ? $"[{localizer.Get("notifications.email.mandatory", locale)}] " : "";
            var outcome = await SendAsync(d, label + rendered.Title, rendered.Body, locale, ct);
            switch (outcome)
            {
                case TeamsSendOutcome.Sent:
                    await FinishAsync(d.Id, "sent", null, now, ct);
                    await outbox.EnqueueAsync(new NotificationDelivered(d.NotificationId, d.UserId, NotificationChannels.Teams), ct);
                    break;
                case TeamsSendOutcome.Transient when now - d.CreatedAt < RetryFor:
                    var tried = d.Attempts + 1;
                    await session.ExecAsync(
                        "update notifications.delivery set attempts = @tried, not_before = @next, last_error = 'teams_unavailable' where id = @id",
                        new { id = d.Id, tried, next = now + EmailDelivery.BackoffAfter(tried) }, ct);
                    break;
                case TeamsSendOutcome.Transient:
                    await FinishAsync(d.Id, "failed", "teams_unavailable", now, ct);
                    await FallBackAsync(d, user, now, ct);
                    logger.LogWarning("Teams delivery {DeliveryId} for tenant {TenantId} gave up after {Attempts} attempts", d.Id, tenantId, d.Attempts);
                    break;
                default:
                    var reason = outcome switch
                    {
                        TeamsSendOutcome.NotInstalled => "teams_not_installed",
                        TeamsSendOutcome.NotLinked => "teams_not_linked",
                        _ => "teams_off",
                    };
                    await FinishAsync(d.Id, "dropped", reason, now, ct);
                    await FallBackAsync(d, user, now, ct);
                    break;
            }
        }
        return due.Count;
    }

    /// <summary>
    /// With quick actions offered (US5 scenario 2), a bot card in the person's chat with the TeamBeats bot; when the bot
    /// is not installed for them or this installation has none, the plain activity notification.
    /// </summary>
    private async Task<TeamsSendOutcome> SendAsync(Due d, string title, string text, string locale, CancellationToken ct)
    {
        if (d.Actions is not null && JsonSerializer.Deserialize<NotificationActions>(d.Actions, JsonDefaults.Options) is { } offered)
        {
            var buttons = await quickActions.MintAsync(d.NotificationId, d.UserId, offered, locale, ct);
            if (buttons.Count > 0)
            {
                var card = new TeamsCard(title, text, d.Link, localizer.Get(QuickActions.OpenLabelKey, locale), buttons,
                    localizer.Get(QuickActions.ReasonLabelKey, locale), Locales.IsRightToLeft(locale));
                var sent = await teams.SendCardAsync(d.UserId, card, ct);
                if (sent is not (TeamsSendOutcome.NotInstalled or TeamsSendOutcome.Unavailable))
                {
                    return sent;
                }
            }
        }
        return await teams.SendActivityAsync(d.UserId, title, text, d.Link, ct);
    }

    /// <summary>Teams could not take it: the message goes by email instead, unless email already has it.</summary>
    private async Task FallBackAsync(Due d, UserInfo? user, DateTimeOffset now, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(user?.Email))
        {
            return;
        }
        await session.ExecAsync(
            """
            insert into notifications.delivery (tenant_id, id, notification_id, user_id, channel, status, address, not_before, created_at)
            select platform.current_tenant_id(), @id, @notificationId, @userId, 'email', 'pending', @address, @now, @now
            where not exists (select 1 from notifications.delivery where notification_id = @notificationId and user_id = @userId and channel = 'email')
            """, new { id = Ids.New(), notificationId = d.NotificationId, userId = d.UserId, address = user.Email.Trim(), now }, ct);
    }

    private Task FinishAsync(Guid id, string status, string? error, DateTimeOffset now, CancellationToken ct) =>
        session.ExecAsync(
            "update notifications.delivery set status = @status, last_error = @error, sent_at = case when @status = 'sent' then @now else sent_at end where id = @id",
            new { id, status, error, now }, ct);
}
