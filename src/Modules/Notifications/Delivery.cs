using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TeamBeats.Localization;
using TeamBeats.Messaging;
using TeamBeats.Modules.Identity.Contracts;
using TeamBeats.Modules.Notifications.Contracts;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Primitives;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.Notifications;

/// <summary>
/// Email delivery for the current tenant (spec 015 US2): due deliveries, daily digests, retries for up to 24 hours,
/// hard bounces and retention. The worker calls these once per tenant with the tenant set in context.
/// </summary>
public sealed class EmailDelivery(
    IDbSession session,
    ITenantContext context,
    IEmailSender sender,
    IOptions<EmailOptions> emailOptions,
    IOptions<HostResolutionOptions> hosts,
    ITenantRegistry tenants,
    ITenantMemberships memberships,
    IUserDirectory users,
    ITenantSettingsReader tenantSettings,
    NotificationSettingsService settings,
    PreferenceService preferences,
    NotificationTemplates templates,
    ILocalizer localizer,
    IOutbox outbox,
    ILogger<EmailDelivery> logger)
{
    public static readonly TimeSpan RetryFor = TimeSpan.FromHours(24);
    public static readonly TimeSpan Retention = TimeSpan.FromDays(90);
    private static readonly TimeSpan[] Backoff =
        [TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(15), TimeSpan.FromHours(1), TimeSpan.FromHours(3)];

    private sealed record Due(Guid Id, Guid NotificationId, Guid UserId, string Address, int Attempts, DateTimeOffset CreatedAt,
        string Category, bool Mandatory, string Template, Dictionary<string, string> Parameters, string Link);

    public static TimeSpan BackoffAfter(int attempts) => Backoff[Math.Min(attempts, Backoff.Length) - 1];

    /// <summary>Sends pending deliveries whose time has come. Returns how many were attempted.</summary>
    public async Task<int> DeliverDueAsync(DateTimeOffset now, int batch, CancellationToken ct)
    {
        var due = await session.QueryAsync(
            """
            select d.id, d.notification_id, d.user_id, d.address, d.attempts, d.created_at,
                   n.category, n.mandatory, n.template_key, n.parameters::text, n.link
            from notifications.delivery d
            join notifications.notification n on n.tenant_id = d.tenant_id and n.id = d.notification_id
            where d.status = 'pending' and d.channel = 'email' and d.not_before <= @now
            order by d.not_before
            limit @batch
            for update of d skip locked
            """,
            new { now, batch },
            r => new Due(r.GetGuid(0), r.GetGuid(1), r.GetGuid(2), r.GetString(3), r.GetInt32(4), r.GetFieldValue<DateTimeOffset>(5),
                r.GetString(6), r.GetBoolean(7), r.GetString(8),
                JsonSerializer.Deserialize<Dictionary<string, string>>(r.GetString(9), JsonDefaults.Options) ?? [], r.GetString(10)),
            ct);

        foreach (var d in due)
        {
            var recipient = await RecipientAsync(d.UserId, d.Address, ct);
            if (recipient.DropReason is { } reason)
            {
                await FinishAsync(d.Id, "dropped", reason, now, ct);
                continue;
            }

            var text = await templates.RenderAsync(d.Template, recipient.Locale, d.Parameters, ct);
            var mandatoryLabel = d.Mandatory ? localizer.Get("notifications.email.mandatory", recipient.Locale) : null;
            var message = await ComposeAsync(d.Address, recipient.Locale, text.Title,
                [new EmailItem(text.Title, text.Body, d.Link)], mandatoryLabel, ct);
            await SendOneAsync(d.Id, d.NotificationId, d.UserId, d.Address, d.Attempts, d.CreatedAt, d.Category, message, now, ct);
        }
        return due.Count;
    }

    /// <summary>Combines each user's digest deliveries into one email at the user's digest time (FR-005).</summary>
    public async Task<int> SendDigestsAsync(DateTimeOffset now, CancellationToken ct)
    {
        var userIds = await session.QueryAsync(
            "select distinct user_id from notifications.delivery where status = 'digest'", null, r => r.GetGuid(0), ct);
        var sent = 0;
        foreach (var userId in userIds)
        {
            var (zone, digestTime, last) = await preferences.DigestScheduleAsync(userId, ct);
            var local = TimeZoneInfo.ConvertTime(now, zone);
            var todayAt = DateOnly.FromDateTime(local.DateTime).ToDateTime(digestTime);
            var dueAt = new DateTimeOffset(todayAt, zone.GetUtcOffset(todayAt)).ToUniversalTime();
            if (now < dueAt || (last is { } l && l >= dueAt))
            {
                continue;
            }

            var rows = await session.QueryAsync(
                """
                select d.id, d.address, n.template_key, n.parameters::text, n.link, n.group_count
                from notifications.delivery d
                join notifications.notification n on n.tenant_id = d.tenant_id and n.id = d.notification_id
                where d.user_id = @userId and d.status = 'digest' and d.created_at <= @now
                order by n.created_at
                for update of d skip locked
                """,
                new { userId, now },
                r => (Id: r.GetGuid(0), Address: r.GetString(1), Template: r.GetString(2),
                    Parameters: JsonSerializer.Deserialize<Dictionary<string, string>>(r.GetString(3), JsonDefaults.Options) ?? [],
                    Link: r.GetString(4), Count: r.GetInt32(5)),
                ct);
            if (rows.Count == 0)
            {
                continue;
            }

            var address = rows[^1].Address;
            var recipient = await RecipientAsync(userId, address, ct);
            if (recipient.DropReason is { } reason)
            {
                foreach (var row in rows)
                {
                    await FinishAsync(row.Id, "dropped", reason, now, ct);
                }
                continue;
            }

            var items = new List<EmailItem>();
            foreach (var row in rows)
            {
                var parameters = new Dictionary<string, string>(row.Parameters) { ["count"] = row.Count.ToString(CultureInfo.InvariantCulture) };
                var text = await templates.RenderAsync(row.Template, recipient.Locale, parameters, ct);
                items.Add(new EmailItem(text.Title, text.Body, row.Link));
            }
            var subject = Fill(localizer.Get("notifications.email.digest_subject", recipient.Locale), "count", items.Count);
            var message = await ComposeAsync(address, recipient.Locale, subject, items, null, ct);
            var result = await sender.SendAsync(message, (await settings.GetEffectiveAsync(ct)).Relay, context.RequireTenant(), ct);
            NotificationsTelemetry.Delivery(context.RequireTenant(), "digest", result.Outcome);
            if (result.Outcome == EmailOutcome.Transient)
            {
                continue; // The digest stays queued and is tried again on the next run.
            }
            foreach (var row in rows)
            {
                await FinishAsync(row.Id, result.Outcome == EmailOutcome.Sent ? "sent" : "failed", result.Error, now, ct);
            }
            if (result.Outcome == EmailOutcome.HardBounce)
            {
                await PauseAddressAsync(userId, address, result.Error ?? "bounce", now, ct);
            }
            await session.ExecAsync(
                """
                insert into notifications.user_settings (tenant_id, user_id, last_digest_at, updated_at)
                values (platform.current_tenant_id(), @userId, @now, @now)
                on conflict (tenant_id, user_id) do update set last_digest_at = excluded.last_digest_at
                """,
                new { userId, now }, ct);
            sent++;
        }
        return sent;
    }

    /// <summary>Removes notifications after 90 days (FR-012) and captured development mail after 7 days.</summary>
    public async Task<int> PurgeAsync(DateTimeOffset now, CancellationToken ct)
    {
        var removed = await session.ExecAsync(
            "delete from notifications.notification where updated_at < @cutoff", new { cutoff = now - Retention }, ct);
        await session.ExecAsync("delete from notifications.captured_mail where captured_at < @cutoff", new { cutoff = now - TimeSpan.FromDays(7) }, ct);
        return removed;
    }

    /// <summary>Sends one message now, outside the queue: the administrator's relay test (US4 scenario 2).</summary>
    public async Task<EmailResult> SendTestAsync(string to, string locale, CancellationToken ct)
    {
        var title = localizer.Get("notifications.email.test_subject", locale);
        var body = localizer.Get("notifications.email.test_body", locale);
        var message = await ComposeAsync(to, locale, title, [new EmailItem(title, body, "/")], null, ct);
        return await sender.SendAsync(message, (await settings.GetEffectiveAsync(ct)).Relay, context.RequireTenant(), ct);
    }

    /// <summary>One email to an address outside the tenant's accounts, now (<see cref="IExternalEmail"/>).</summary>
    public async Task<string> SendExternalAsync(ExternalEmailRequest request, CancellationToken ct)
    {
        if (!request.Link.StartsWith('/') || request.Link.StartsWith("//", StringComparison.Ordinal) || request.Link.Contains("://", StringComparison.Ordinal))
        {
            throw new ArgumentException("The link must be a path on the tenant host.", nameof(request));
        }
        var tenantLocale = await tenantSettings.GetLocaleSettingsAsync(ct);
        var locale = LocaleResolver.Resolve(request.Locale, tenantLocale.EnabledLocales, tenantLocale.DefaultLocale);
        var text = await templates.RenderAsync(request.TemplateKey, locale, request.Parameters ?? new Dictionary<string, string>(), ct);
        var message = await ComposeAsync(request.To, locale, text.Title, [new EmailItem(text.Title, text.Body, request.Link)], null, ct);
        var tenantId = context.RequireTenant();
        var result = await sender.SendAsync(message, (await settings.GetEffectiveAsync(ct)).Relay, tenantId, ct);
        NotificationsTelemetry.Delivery(tenantId, "external", result.Outcome);
        return result.Outcome switch
        {
            EmailOutcome.Sent => ExternalEmailOutcomes.Sent,
            EmailOutcome.HardBounce => ExternalEmailOutcomes.Bounced,
            _ => ExternalEmailOutcomes.Retry,
        };
    }

    private async Task SendOneAsync(Guid deliveryId, Guid notificationId, Guid userId, string address, int attempts, DateTimeOffset createdAt,
        string category, EmailMessage message, DateTimeOffset now, CancellationToken ct)
    {
        var tenantId = context.RequireTenant();
        var result = await sender.SendAsync(message, (await settings.GetEffectiveAsync(ct)).Relay, tenantId, ct);
        NotificationsTelemetry.Delivery(tenantId, category, result.Outcome);
        switch (result.Outcome)
        {
            case EmailOutcome.Sent:
                await FinishAsync(deliveryId, "sent", null, now, ct);
                await outbox.EnqueueAsync(new NotificationDelivered(notificationId, userId, NotificationChannels.Email), ct);
                break;
            case EmailOutcome.HardBounce:
                await FinishAsync(deliveryId, "failed", result.Error, now, ct);
                await PauseAddressAsync(userId, address, result.Error ?? "bounce", now, ct);
                break;
            default:
                var tried = attempts + 1;
                if (now - createdAt >= RetryFor)
                {
                    await FinishAsync(deliveryId, "failed", result.Error, now, ct);
                    // Operators are alerted from this log entry and the failure counter (spec 015 US2 scenario 4).
                    logger.LogError("Email delivery {DeliveryId} for tenant {TenantId} failed after {Attempts} attempts: {Error}", deliveryId, tenantId, tried, result.Error);
                    break;
                }
                await session.ExecAsync(
                    "update notifications.delivery set attempts = @tried, not_before = @next, last_error = @error where id = @deliveryId",
                    new { deliveryId, tried, next = now + BackoffAfter(tried), error = result.Error }, ct);
                logger.LogWarning("Email delivery {DeliveryId} will retry: {Error}", deliveryId, result.Error);
                break;
        }
    }

    private sealed record Recipient(string Locale, string? DropReason);

    private async Task<Recipient> RecipientAsync(Guid userId, string address, CancellationToken ct)
    {
        var tenantLocale = await tenantSettings.GetLocaleSettingsAsync(ct);
        var membership = await memberships.GetAsync(userId, ct);
        if (membership is not { Status: MembershipStatus.Active })
        {
            return new Recipient(tenantLocale.DefaultLocale, "member_inactive");
        }
        if (await session.ScalarAsync<bool>("select exists (select 1 from notifications.address_state where address = @address)", new { address }, ct))
        {
            return new Recipient(tenantLocale.DefaultLocale, "address_paused");
        }
        var user = await users.GetAsync(userId, ct);
        return new Recipient(LocaleResolver.Resolve(user?.PreferredLocale, tenantLocale.EnabledLocales, tenantLocale.DefaultLocale), null);
    }

    private async Task PauseAddressAsync(Guid userId, string address, string reason, DateTimeOffset now, CancellationToken ct)
    {
        await session.ExecAsync(
            """
            insert into notifications.address_state (tenant_id, address, bounced_at, reason)
            values (platform.current_tenant_id(), @address, @now, @reason)
            on conflict (tenant_id, address) do update set bounced_at = excluded.bounced_at, reason = excluded.reason
            """,
            new { address, now, reason }, ct);
        await outbox.EnqueueAsync(new NotificationBounced(userId, NotificationChannels.Email), ct);
    }

    private Task FinishAsync(Guid deliveryId, string status, string? error, DateTimeOffset now, CancellationToken ct) =>
        session.ExecAsync(
            "update notifications.delivery set status = @status, last_error = @error, sent_at = case when @status = 'sent' then @now else sent_at end where id = @deliveryId",
            new { deliveryId, status, error, now }, ct);

    private sealed record EmailItem(string Title, string Body, string Link);

    private async Task<EmailMessage> ComposeAsync(string to, string locale, string subject, IReadOnlyList<EmailItem> items, string? mandatoryLabel, CancellationToken ct)
    {
        var tenantId = context.RequireTenant();
        var origin = await TenantOriginAsync(hosts.Value, tenants, tenantId, ct);
        var effective = await settings.GetEffectiveAsync(ct);
        var fromName = effective.SenderName ?? localizer.Get("notifications.email.default_sender", locale);
        var open = localizer.Get("notifications.email.open", locale);
        var footer = localizer.Get("notifications.email.footer", locale);
        var dir = LocaleResolver.Direction(locale);

        var text = new StringBuilder();
        var html = new StringBuilder();
        html.Append(CultureInfo.InvariantCulture, $"<!doctype html><html lang=\"{locale}\" dir=\"{dir}\"><body style=\"font-family:sans-serif\">");
        if (mandatoryLabel is not null)
        {
            text.AppendLine(CultureInfo.InvariantCulture, $"[{mandatoryLabel}]").AppendLine();
            html.Append(CultureInfo.InvariantCulture, $"<p><strong>{Encode(mandatoryLabel)}</strong></p>");
        }
        foreach (var item in items)
        {
            var url = origin + item.Link;
            text.AppendLine(item.Title).AppendLine(item.Body).AppendLine(CultureInfo.InvariantCulture, $"{open}: {url}").AppendLine();
            html.Append(CultureInfo.InvariantCulture,
                $"<h2>{Encode(item.Title)}</h2><p>{Encode(item.Body)}</p><p><a href=\"{Encode(url)}\">{Encode(open)}</a></p>");
        }
        text.AppendLine(footer);
        html.Append(CultureInfo.InvariantCulture, $"<p style=\"color:#555\">{Encode(footer)}</p></body></html>");
        return new EmailMessage(to, emailOptions.Value.FromAddress, fromName, subject, text.ToString(), html.ToString(), locale);
    }

    /// <summary>The https origin of the tenant's own host, for links in messages that leave TeamBeats.</summary>
    internal static async Task<string> TenantOriginAsync(HostResolutionOptions h, ITenantRegistry tenants, Guid tenantId, CancellationToken ct)
    {
        if (h.Mode == InstallationMode.Dedicated)
        {
            return $"https://{h.BaseDomain}";
        }
        var tenant = await tenants.FindByIdAsync(tenantId, ct) ?? throw new InvalidOperationException("Unknown tenant.");
        return $"https://{tenant.Key}.{h.BaseDomain}";
    }

    private static string Encode(string value) => WebUtility.HtmlEncode(value);

    private static string Fill(string text, string name, int value) => text.Replace("{" + name + "}", value.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal);
}

/// <summary>The <see cref="IExternalEmail"/> contract over <see cref="EmailDelivery"/>.</summary>
public sealed class ExternalEmail(EmailDelivery delivery) : IExternalEmail
{
    public Task<string> SendAsync(ExternalEmailRequest request, CancellationToken ct = default) => delivery.SendExternalAsync(request, ct);
}
