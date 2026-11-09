using System.Text.Json;
using Microsoft.Extensions.Logging;
using TeamBeats.Modules.Identity.Contracts;
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
        foreach (var userId in request.Recipients.Distinct())
        {
            var membership = await memberships.GetAsync(userId, ct);
            if (membership is not { Status: MembershipStatus.Active })
            {
                // Another tenant's user, or a disabled member: nothing is sent (spec 015 section 7, NOT-011).
                skipped++;
                continue;
            }
            if ((await users.GetAsync(userId, ct))?.IsTest == true)
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

            var id = Ids.New();
            await session.ExecAsync(
                """
                insert into notifications.notification
                  (tenant_id, id, user_id, category, mandatory, urgent, template_key, parameters, link, dedupe_key, group_key, created_at, updated_at)
                values (platform.current_tenant_id(), @id, @userId, @category, @mandatory, @urgent, @template, @parameters, @link, @dedupe, @group, @now, @now)
                """,
                new
                {
                    id, userId, category = request.Category, mandatory, urgent = request.Urgent, template = request.TemplateKey,
                    parameters, link = request.Link, dedupe = request.DedupeKey, group = request.GroupKey, now,
                }, ct);
            created++;

            await QueueEmailAsync(id, userId, request, mandatory, now, ct);
        }

        NotificationsTelemetry.Sent(context.RequireTenant(), request.Category, created, grouped, duplicates, skipped);
        if (skipped > 0)
        {
            logger.LogInformation("Notification {Category}: {Skipped} recipients skipped (not active members of this tenant)", request.Category, skipped);
        }
        return new SendResult(created, grouped, duplicates, skipped);
    }

    private async Task QueueEmailAsync(Guid notificationId, Guid userId, NotificationRequest request, bool mandatory, DateTimeOffset now, CancellationToken ct)
    {
        var choice = await preferences.ChannelChoiceAsync(userId, request.Category, mandatory, ct);
        if (!choice.Email)
        {
            return;
        }

        var user = await users.GetAsync(userId, ct);
        if (user is null || string.IsNullOrWhiteSpace(user.Email))
        {
            return;
        }

        // Digest applies to non-urgent messages only; quiet hours hold everything except urgent mandatory messages.
        var digest = choice.Digest && !request.Urgent;
        var notBefore = digest || (request.Urgent && mandatory) ? now : await preferences.EndOfQuietHoursAsync(userId, now, ct);
        await session.ExecAsync(
            """
            insert into notifications.delivery (tenant_id, id, notification_id, user_id, channel, status, address, not_before, created_at)
            values (platform.current_tenant_id(), @id, @notificationId, @userId, 'email', @status, @address, @notBefore, @now)
            """,
            new { id = Ids.New(), notificationId, userId, status = digest ? "digest" : "pending", address = user.Email.Trim(), notBefore, now }, ct);
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
