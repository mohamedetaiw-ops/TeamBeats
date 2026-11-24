using Microsoft.Extensions.Logging;
using System.Text.Json;
using TeamBeats.Entitlements.Contracts;
using TeamBeats.Localization;
using TeamBeats.Messaging;
using TeamBeats.Modules.Identity.Contracts;
using TeamBeats.Modules.Integrations.Contracts;
using TeamBeats.Modules.Notifications.Contracts;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Primitives;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.Notifications;

/// <summary>
/// SMS for mandatory messages (spec 015 US6 scenario 2, FR-007, FR-020). The SMS carries the message title and a link,
/// never the body, so nothing Confidential reaches a phone screen. The number is read from the directory when the SMS
/// goes out and is never stored by this module. Each SMS counts against the monthly limit once; technical failures retry
/// for up to 30 minutes (spec 015 section 9) with the same idempotency key, so a person never gets two.
/// </summary>
public sealed class SmsDelivery(
    IDbSession session,
    ITenantContext context,
    ISmsMessaging sms,
    IDirectoryReader directory,
    ITenantMemberships memberships,
    IUserDirectory users,
    ITenantSettingsReader tenantSettings,
    NotificationTemplates templates,
    ILocalizer localizer,
    IEntitlementService entitlements,
    IOutbox outbox,
    ILogger<SmsDelivery> logger)
{
    public static readonly TimeSpan RetryFor = TimeSpan.FromMinutes(30);

    private sealed record Due(Guid Id, Guid NotificationId, Guid UserId, int Attempts, DateTimeOffset CreatedAt, string Template,
        Dictionary<string, string> Parameters, string Link);

    public async Task<int> DeliverDueAsync(DateTimeOffset now, int batch, CancellationToken ct)
    {
        var due = await session.QueryAsync(
            """
            select d.id, d.notification_id, d.user_id, d.attempts, d.created_at, n.template_key, n.parameters::text, n.link
            from notifications.delivery d
            join notifications.notification n on n.tenant_id = d.tenant_id and n.id = d.notification_id
            where d.status = 'pending' and d.channel = 'sms' and d.not_before <= @now
            order by d.not_before
            limit @batch
            for update of d skip locked
            """,
            new { now, batch },
            r => new Due(r.GetGuid(0), r.GetGuid(1), r.GetGuid(2), r.GetInt32(3), r.GetFieldValue<DateTimeOffset>(4), r.GetString(5),
                JsonSerializer.Deserialize<Dictionary<string, string>>(r.GetString(6), JsonDefaults.Options) ?? [], r.GetString(7)),
            ct);
        if (due.Count == 0)
        {
            return 0;
        }
        var tenantId = context.RequireTenant();
        var tenantLocale = await tenantSettings.GetLocaleSettingsAsync(ct);
        var phones = await directory.MobilePhonesAsync(due.Select(d => d.UserId).ToList(), ct);
        foreach (var d in due)
        {
            if (await memberships.GetAsync(d.UserId, ct) is not { Status: MembershipStatus.Active })
            {
                await FinishAsync(d.Id, "dropped", "member_inactive", now, ct);
                continue;
            }
            if (!phones.TryGetValue(d.UserId, out var phone))
            {
                await FinishAsync(d.Id, "dropped", "sms_no_number", now, ct);
                continue;
            }
            if (d.Attempts == 0 && !(await entitlements.ConsumeAsync(Capabilities.NotificationsSmsMonthlyMessages, 1, ct)).Allowed)
            {
                await FinishAsync(d.Id, "dropped", "sms_quota", now, ct);
                continue;
            }
            var user = await users.GetAsync(d.UserId, ct);
            var locale = LocaleResolver.Resolve(user?.PreferredLocale, tenantLocale.EnabledLocales, tenantLocale.DefaultLocale);
            var rendered = await templates.RenderAsync(d.Template, locale, d.Parameters, ct);
            var text = $"[{localizer.Get("notifications.email.mandatory", locale)}] {rendered.Title}";
            var outcome = await sms.SendAsync(phone, text, d.Link, $"notification-sms:{d.Id:N}", ct);
            switch (outcome)
            {
                case SmsSendOutcome.Sent:
                    await FinishAsync(d.Id, "sent", null, now, ct);
                    await outbox.EnqueueAsync(new NotificationDelivered(d.NotificationId, d.UserId, NotificationChannels.Sms), ct);
                    break;
                case SmsSendOutcome.Transient when now - d.CreatedAt < RetryFor:
                    var tried = d.Attempts + 1;
                    await session.ExecAsync(
                        "update notifications.delivery set attempts = @tried, not_before = @next, last_error = 'sms_unavailable' where id = @id",
                        new { id = d.Id, tried, next = now + EmailDelivery.BackoffAfter(tried) }, ct);
                    break;
                default:
                    var reason = outcome switch
                    {
                        SmsSendOutcome.NumberRefused => "sms_number_refused",
                        SmsSendOutcome.Unavailable => "sms_off",
                        SmsSendOutcome.Transient => "sms_unavailable",
                        _ => "sms_failed",
                    };
                    await FinishAsync(d.Id, outcome is SmsSendOutcome.Unavailable or SmsSendOutcome.NumberRefused ? "dropped" : "failed", reason, now, ct);
                    // Nothing reached the gateway for these, so the SMS does not count against the limit.
                    if (outcome is SmsSendOutcome.Unavailable or SmsSendOutcome.NumberRefused)
                    {
                        await entitlements.ReleaseAsync(Capabilities.NotificationsSmsMonthlyMessages, 1, ct);
                    }
                    else
                    {
                        logger.LogWarning("SMS delivery {DeliveryId} for tenant {TenantId} ended with {Outcome}", d.Id, tenantId, outcome);
                    }
                    break;
            }
        }
        return due.Count;
    }

    private Task FinishAsync(Guid id, string status, string? error, DateTimeOffset now, CancellationToken ct) =>
        session.ExecAsync(
            "update notifications.delivery set status = @status, last_error = @error, sent_at = case when @status = 'sent' then @now else sent_at end where id = @id",
            new { id, status, error, now }, ct);
}
