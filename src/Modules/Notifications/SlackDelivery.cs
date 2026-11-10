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
/// Slack direct messages for the current tenant (spec 015 US8 scenario 3, FR-019; spec 017 US8). Each due delivery is
/// written from its template in the person's language and sent through the Slack connector. Technical failures retry
/// with the email backoff for up to 2 hours (spec 015 section 9). When Slack says the person left the workspace, the
/// link is dropped and the message falls back to email if it was not already going there (spec 017 US8 scenario 2).
/// </summary>
public sealed class SlackDelivery(
    IDbSession session,
    ITenantContext context,
    ISlackMessaging slack,
    ITenantMemberships memberships,
    IUserDirectory users,
    ITenantSettingsReader tenantSettings,
    NotificationTemplates templates,
    ILocalizer localizer,
    IOutbox outbox,
    ILogger<SlackDelivery> logger)
{
    public static readonly TimeSpan RetryFor = TimeSpan.FromHours(2);

    private sealed record Due(Guid Id, Guid NotificationId, Guid UserId, int Attempts, DateTimeOffset CreatedAt, bool Mandatory, string Template,
        Dictionary<string, string> Parameters, string Link);

    public async Task<int> DeliverDueAsync(DateTimeOffset now, int batch, CancellationToken ct)
    {
        var due = await session.QueryAsync(
            """
            select d.id, d.notification_id, d.user_id, d.attempts, d.created_at, n.mandatory, n.template_key, n.parameters::text, n.link
            from notifications.delivery d
            join notifications.notification n on n.tenant_id = d.tenant_id and n.id = d.notification_id
            where d.status = 'pending' and d.channel = 'slack' and d.not_before <= @now
            order by d.not_before
            limit @batch
            for update of d skip locked
            """,
            new { now, batch },
            r => new Due(r.GetGuid(0), r.GetGuid(1), r.GetGuid(2), r.GetInt32(3), r.GetFieldValue<DateTimeOffset>(4), r.GetBoolean(5), r.GetString(6),
                JsonSerializer.Deserialize<Dictionary<string, string>>(r.GetString(7), JsonDefaults.Options) ?? [], r.GetString(8)),
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
            var text = $"{label}{rendered.Title}\n{rendered.Body}";
            var outcome = await slack.SendDirectAsync(d.UserId, text, d.Link, $"notification:{d.Id:N}", ct);
            switch (outcome)
            {
                case SlackSendOutcome.Sent:
                    await FinishAsync(d.Id, "sent", null, now, ct);
                    await outbox.EnqueueAsync(new NotificationDelivered(d.NotificationId, d.UserId, NotificationChannels.Slack), ct);
                    break;
                case SlackSendOutcome.Transient when now - d.CreatedAt < RetryFor:
                    var tried = d.Attempts + 1;
                    await session.ExecAsync(
                        "update notifications.delivery set attempts = @tried, not_before = @next, last_error = 'slack_unavailable' where id = @id",
                        new { id = d.Id, tried, next = now + EmailDelivery.BackoffAfter(tried) }, ct);
                    break;
                case SlackSendOutcome.UserGone:
                    await FinishAsync(d.Id, "dropped", "slack_user_left", now, ct);
                    await FallBackAsync(d, user, now, ct);
                    break;
                case SlackSendOutcome.NotLinked or SlackSendOutcome.Unavailable:
                    await FinishAsync(d.Id, "dropped", outcome == SlackSendOutcome.NotLinked ? "slack_not_linked" : "slack_unavailable", now, ct);
                    break;
                default:
                    await FinishAsync(d.Id, "failed", "slack_failed", now, ct);
                    logger.LogWarning("Slack delivery {DeliveryId} for tenant {TenantId} failed: {Outcome}", d.Id, tenantId, outcome);
                    break;
            }
        }
        return due.Count;
    }

    /// <summary>The person left the Slack workspace: the message goes by email instead, unless email already has it.</summary>
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
