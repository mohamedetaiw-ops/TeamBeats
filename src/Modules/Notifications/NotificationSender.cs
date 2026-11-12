using System.Text.Json;
using Microsoft.Extensions.Logging;
using TeamBeats.Modules.Identity.Contracts;
using TeamBeats.Modules.Integrations.Contracts;
using TeamBeats.Modules.Notifications.Contracts;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Primitives;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.Notifications;

/// <summary>
/// Spec 015 send contract. One transaction with the caller: the in-app row and any email delivery row are written
/// together, so a rolled back business change never notifies. Only active members of the current tenant receive
/// anything (NOT-011, NOT-023); the tenant always comes from context.
/// </summary>
public sealed class NotificationSender(
    IDbSession session,
    ITenantContext context,
    IClock clock,
    ITenantMemberships memberships,
    IUserDirectory users,
    NotificationSettingsService settings,
    PreferenceService preferences,
    ISlackMessaging slack,
    ILogger<NotificationSender> logger) : INotificationSender
{
    public static readonly TimeSpan DedupeWindow = TimeSpan.FromHours(1);
    public static readonly TimeSpan GroupWindow = TimeSpan.FromMinutes(15);
    public const int MaxParameterLength = 500;

    public async Task<SendResult> SendAsync(NotificationRequest request, CancellationToken ct = default)
    {
        context.RequireTenant();
        Validate(request);
        var tenantSettings = await settings.GetEffectiveAsync(ct);
        var mandatory = tenantSettings.IsMandatory(request.Category);
        var now = clock.UtcNow;
        var parameters = new Jsonb(JsonSerializer.Serialize(request.Parameters ?? new Dictionary<string, string>(), JsonDefaults.Options));

        int created = 0, grouped = 0, duplicates = 0, skipped = 0;
        var recipients = request.Recipients.Distinct().ToList();
        // Members and accounts are read for the whole batch at once, so a burst of 100,000 (a critical announcement in
        // batches of 500) stays within its time budget (spec 015 section 15).
        var members = await memberships.GetManyAsync(recipients, ct);
        var accounts = await users.GetManyAsync(recipients, ct);
        var rows = new List<(Guid Id, Guid UserId)>();
        foreach (var userId in recipients)
        {
            if (!members.TryGetValue(userId, out var membership) || membership.Status != MembershipStatus.Active)
            {
                // Another tenant's user, or a disabled member: nothing is sent (spec 015 section 7, NOT-011).
                skipped++;
                continue;
            }
            if (accounts.GetValueOrDefault(userId)?.IsTest == true)
            {
                // A synthetic evaluation persona is never notified (spec 034 FR-006).
                skipped++;
                continue;
            }

            if (request.DedupeKey is { } dedupe && await session.ScalarAsync<bool>(
                    """
                    select exists (select 1 from notifications.notification
                                   where user_id = @userId and dedupe_key = @dedupe and created_at > @since)
                    """,
                    new { userId, dedupe, since = now - DedupeWindow }, ct))
            {
                duplicates++;
                continue;
            }

            if (request.GroupKey is { } group)
            {
                var folded = await session.ExecAsync(
                    """
                    update notifications.notification
                    set group_count = group_count + 1, updated_at = @now, parameters = @parameters, link = @link
                    where user_id = @userId and group_key = @group and read_at is null and updated_at > @since
                    """,
                    new { userId, group, now, since = now - GroupWindow, parameters, link = request.Link }, ct);
                if (folded > 0)
                {
                    // The first notification of the group already went out on the other channels.
                    grouped++;
                    continue;
                }
            }
            rows.Add((Ids.New(), userId));
        }

        if (rows.Count > 0)
        {
            await session.ExecAsync(
                """
                insert into notifications.notification
                  (tenant_id, id, user_id, category, mandatory, urgent, template_key, parameters, link, dedupe_key, group_key, created_at, updated_at)
                select platform.current_tenant_id(), r.id, r.user_id, @category, @mandatory, @urgent, @template, @parameters, @link, @dedupe, @group, @now, @now
                from unnest(@ids, @userIds) as r(id, user_id)
                """,
                new
                {
                    ids = rows.Select(r => r.Id).ToArray(), userIds = rows.Select(r => r.UserId).ToArray(), category = request.Category, mandatory,
                    urgent = request.Urgent, template = request.TemplateKey, parameters, link = request.Link, dedupe = request.DedupeKey, group = request.GroupKey, now,
                }, ct);
            created = rows.Count;
            await QueueEmailAsync(rows, accounts, request, mandatory, now, ct);
        }

        NotificationsTelemetry.Sent(context.RequireTenant(), request.Category, created, grouped, duplicates, skipped);
        if (skipped > 0)
        {
            logger.LogInformation("Notification {Category}: {Skipped} recipients skipped (not active members of this tenant)", request.Category, skipped);
        }
        return new SendResult(created, grouped, duplicates, skipped);
    }

    private async Task QueueEmailAsync(IReadOnlyList<(Guid Id, Guid UserId)> rows, IReadOnlyDictionary<Guid, UserInfo> accounts, NotificationRequest request,
        bool mandatory, DateTimeOffset now, CancellationToken ct)
    {
        var deliveries = new List<(Guid NotificationId, Guid UserId, string Channel, string Status, string? Address, DateTimeOffset NotBefore)>();
        var slackAvailable = true;
        // One read for the batch: who has a device with push. The channel itself checks the package at delivery.
        var pushUsers = (await session.QueryAsync(
            "select distinct user_id from notifications.push_subscription where user_id = any(@userIds)",
            new { userIds = rows.Select(r => r.UserId).ToArray() }, r => r.GetGuid(0), ct)).ToHashSet();
        foreach (var (notificationId, userId) in rows)
        {
            var choice = await preferences.ChannelChoiceAsync(userId, request.Category, mandatory, ct);
            DateTimeOffset? release = null;
            async Task<DateTimeOffset> ReleaseAsync() =>
                release ??= request.Urgent && mandatory ? now : await preferences.EndOfQuietHoursAsync(userId, now, ct);

            if (choice.Email && accounts.GetValueOrDefault(userId) is { } user && !string.IsNullOrWhiteSpace(user.Email))
            {
                // Digest applies to non-urgent messages only; quiet hours hold everything except urgent mandatory messages.
                var digest = choice.Digest && !request.Urgent;
                deliveries.Add((notificationId, userId, NotificationChannels.Email, digest ? "digest" : "pending", user.Email.Trim(), digest ? now : await ReleaseAsync()));
            }
            // Slack direct messages (US8 scenario 3) for people who linked their account; never in the digest.
            if (choice.Slack && slackAvailable && await slack.StatusAsync(userId, ct) is var status && (slackAvailable = status.Available) && status.Linked)
            {
                deliveries.Add((notificationId, userId, NotificationChannels.Slack, "pending", null, await ReleaseAsync()));
            }
            // Push (spec 030 US2) to people with at least one device that allowed it; never in the digest.
            if (choice.Push && pushUsers.Contains(userId))
            {
                deliveries.Add((notificationId, userId, NotificationChannels.Push, "pending", null, await ReleaseAsync()));
            }
        }
        if (deliveries.Count == 0)
        {
            return;
        }
        await session.ExecAsync(
            """
            insert into notifications.delivery (tenant_id, id, notification_id, user_id, channel, status, address, not_before, created_at)
            select platform.current_tenant_id(), d.id, d.notification_id, d.user_id, d.channel, d.status, d.address, d.not_before, @now
            from unnest(@ids, @notificationIds, @userIds, @channels, @statuses, @addresses, @notBefores)
              as d(id, notification_id, user_id, channel, status, address, not_before)
            """,
            new
            {
                ids = deliveries.Select(_ => Ids.New()).ToArray(), notificationIds = deliveries.Select(d => d.NotificationId).ToArray(),
                userIds = deliveries.Select(d => d.UserId).ToArray(), channels = deliveries.Select(d => d.Channel).ToArray(),
                statuses = deliveries.Select(d => d.Status).ToArray(), addresses = deliveries.Select(d => d.Address).ToArray(),
                notBefores = deliveries.Select(d => d.NotBefore).ToArray(), now,
            }, ct);
    }

    private static void Validate(NotificationRequest request)
    {
        var errors = new ValidationErrors();
        if (NotificationCategories.Find(request.Category) is null)
        {
            errors.Add("category", "validation.notification.category_unknown");
        }
        if (string.IsNullOrWhiteSpace(request.TemplateKey) || !request.TemplateKey.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '.'))
        {
            errors.Add("templateKey", "validation.notification.template_invalid");
        }
        // Links stay on the tenant host (spec 015 section 14): a path, never an absolute or protocol-relative URL.
        if (!request.Link.StartsWith('/') || request.Link.StartsWith("//", StringComparison.Ordinal) || request.Link.Contains('\\'))
        {
            errors.Add("link", "validation.notification.link_not_relative");
        }
        if (request.Parameters is { } p && p.Any(kv => kv.Value.Length > MaxParameterLength))
        {
            errors.Add("parameters", "validation.notification.parameter_too_long");
        }
        errors.ThrowIfAny();
    }
}
