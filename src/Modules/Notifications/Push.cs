using System.Text.Json;
using Microsoft.Extensions.Logging;
using TeamBeats.Entitlements.Contracts;
using TeamBeats.Localization;
using TeamBeats.Messaging;
using TeamBeats.Modules.Analytics.Contracts;
using TeamBeats.Modules.Identity.Contracts;
using TeamBeats.Modules.Integrations.Contracts;
using TeamBeats.Modules.Notifications.Contracts;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Primitives;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.Notifications;

/// <summary>What the browser gives after <c>pushManager.subscribe</c>, and the platform family the app read from it.</summary>
public sealed record PushSubscribeRequest(string? Endpoint, PushKeys? Keys, string? Platform);

public sealed record PushKeys(string? P256dh, string? Auth);

public sealed record PushUnsubscribeRequest(string? Endpoint);

/// <summary>Push for this person (spec 030 US2): whether it can be used here, the key to subscribe with, and how many devices have it.</summary>
public sealed record PushStatus(bool Available, string? PublicKey, int Devices);

/// <summary>
/// Push subscriptions (spec 030 US2, FR-008). A subscription belongs to the person and the session that made it, so
/// signing out or revoking the session removes it. Only push services on the allow list are accepted, at most
/// <see cref="MaxPerUser"/> devices per person, the oldest dropped first.
/// </summary>
public sealed class PushSubscriptions(IDbSession session, ITenantContext context, IClock clock, IWebPush push, IEntitlementService entitlements,
    IOutbox outbox, IAnalyticsEvents analytics)
{
    public const int MaxPerUser = 10;

    public static readonly IReadOnlySet<string> Platforms = new HashSet<string>(StringComparer.Ordinal) { "android", "ios", "windows", "macos", "linux", "other" };

    public async Task<PushStatus> StatusAsync(Guid userId, CancellationToken ct)
    {
        var available = push.PublicKey is not null && await entitlements.IsEnabledAsync(Capabilities.MobilePush, ct);
        var devices = await session.ScalarAsync<long>("select count(*) from notifications.push_subscription where user_id = @userId", new { userId }, ct);
        return new PushStatus(available, available ? push.PublicKey : null, (int)devices);
    }

    public async Task<PushStatus> SubscribeAsync(Guid userId, PushSubscribeRequest request, CancellationToken ct)
    {
        await entitlements.DemandAsync(Capabilities.MobilePush, ct);
        if (push.PublicKey is null)
        {
            throw AppException.Conflict("conflict.push.unavailable");
        }
        var errors = new ValidationErrors();
        var endpoint = request.Endpoint?.Trim() ?? "";
        if (!push.IsAllowedEndpoint(endpoint))
        {
            errors.Add("endpoint", "validation.push.endpoint");
        }
        if (!KeyOfLength(request.Keys?.P256dh, 65) || !KeyOfLength(request.Keys?.Auth, 16))
        {
            errors.Add("keys", "validation.push.keys");
        }
        var platform = request.Platform is { } p && Platforms.Contains(p) ? p : "other";
        errors.ThrowIfAny();

        var now = clock.UtcNow;
        // The same browser subscribing again, for this person or after someone else signed out on it, takes the row over.
        var inserted = await session.ScalarAsync<bool>(
            """
            insert into notifications.push_subscription (tenant_id, id, user_id, session_id, endpoint, p256dh, auth, platform, created_at)
            values (platform.current_tenant_id(), @id, @userId, @sessionId, @endpoint, @p256dh, @auth, @platform, @now)
            on conflict (tenant_id, endpoint) do update
              set user_id = excluded.user_id, session_id = excluded.session_id, p256dh = excluded.p256dh, auth = excluded.auth,
                  platform = excluded.platform, created_at = excluded.created_at
            returning (xmax = 0)
            """,
            new { id = Ids.New(), userId, sessionId = context.SessionId, endpoint, p256dh = request.Keys!.P256dh!.Trim(), auth = request.Keys.Auth!.Trim(), platform, now }, ct);
        await session.ExecAsync(
            """
            delete from notifications.push_subscription
            where user_id = @userId and id not in (
              select id from notifications.push_subscription where user_id = @userId order by created_at desc, id limit @max)
            """, new { userId, max = MaxPerUser }, ct);
        if (inserted)
        {
            await outbox.EnqueueAsync(new PushSubscriptionCreated(userId, platform), ct);
            await analytics.EmitAsync(AnalyticsEvents.MobilePushOptIn, new Dictionary<string, object?> { ["platform"] = platform }, ct);
        }
        return await StatusAsync(userId, ct);
    }

    /// <summary>Turns push off for one device of the person. Another person's device is never touched.</summary>
    public async Task<PushStatus> UnsubscribeAsync(Guid userId, PushUnsubscribeRequest request, CancellationToken ct)
    {
        var removed = await session.ExecAsync(
            "delete from notifications.push_subscription where user_id = @userId and endpoint = @endpoint",
            new { userId, endpoint = request.Endpoint?.Trim() ?? "" }, ct);
        if (removed > 0)
        {
            await outbox.EnqueueAsync(new PushSubscriptionRemoved(userId, "turned_off"), ct);
        }
        return await StatusAsync(userId, ct);
    }

    public async Task RemoveForSessionAsync(Guid userId, Guid sessionId, string reason, CancellationToken ct)
    {
        var removed = await session.ExecAsync(
            "delete from notifications.push_subscription where user_id = @userId and session_id = @sessionId", new { userId, sessionId }, ct);
        if (removed > 0)
        {
            await outbox.EnqueueAsync(new PushSubscriptionRemoved(userId, reason), ct);
        }
    }

    public async Task RemoveForUserAsync(Guid userId, string reason, CancellationToken ct)
    {
        var removed = await session.ExecAsync("delete from notifications.push_subscription where user_id = @userId", new { userId }, ct);
        if (removed > 0)
        {
            await outbox.EnqueueAsync(new PushSubscriptionRemoved(userId, reason), ct);
        }
    }

    private static bool KeyOfLength(string? value, int length)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 200)
        {
            return false;
        }
        try
        {
            var s = value.Trim().Replace('-', '+').Replace('_', '/');
            return Convert.FromBase64String(s.PadRight(s.Length + (4 - s.Length % 4) % 4, '=')).Length == length;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}

/// <summary>
/// Push deliveries for the current tenant (spec 030 US2, spec 015 FR-007). The message names only the category and
/// says to open TeamBeats, so no Confidential content, or anything the item says, ever leaves in a push (NOT-007); the
/// tap opens the item's page in the app. Title and body keep to 60 and 120 characters (spec 030 section 6). Each of the
/// person's devices gets it; a device the push service reports gone is removed at once. Technical failures retry with
/// the email backoff for up to 2 hours.
/// </summary>
public sealed class PushDelivery(
    IDbSession session,
    ITenantContext context,
    IWebPush push,
    IEntitlementService entitlements,
    ITenantMemberships memberships,
    IUserDirectory users,
    ITenantSettingsReader tenantSettings,
    ILocalizer localizer,
    IOutbox outbox,
    ILogger<PushDelivery> logger)
{
    public static readonly TimeSpan RetryFor = TimeSpan.FromHours(2);
    public static readonly TimeSpan TimeToLive = TimeSpan.FromHours(24);
    public const int MaxTitle = 60;
    public const int MaxBody = 120;

    private sealed record Due(Guid Id, Guid NotificationId, Guid UserId, int Attempts, DateTimeOffset CreatedAt, bool Mandatory, bool Urgent, string Category,
        string Link);

    private sealed record Device(Guid Id, string Endpoint, string P256dh, string Auth);

    public async Task<int> DeliverDueAsync(DateTimeOffset now, int batch, CancellationToken ct)
    {
        var due = await session.QueryAsync(
            """
            select d.id, d.notification_id, d.user_id, d.attempts, d.created_at, n.mandatory, n.urgent, n.category, n.link
            from notifications.delivery d
            join notifications.notification n on n.tenant_id = d.tenant_id and n.id = d.notification_id
            where d.status = 'pending' and d.channel = 'push' and d.not_before <= @now
            order by d.not_before
            limit @batch
            for update of d skip locked
            """,
            new { now, batch },
            r => new Due(r.GetGuid(0), r.GetGuid(1), r.GetGuid(2), r.GetInt32(3), r.GetFieldValue<DateTimeOffset>(4), r.GetBoolean(5), r.GetBoolean(6),
                r.GetString(7), r.GetString(8)),
            ct);
        if (due.Count == 0)
        {
            return 0;
        }
        if (push.PublicKey is null || !await entitlements.IsEnabledAsync(Capabilities.MobilePush, ct))
        {
            foreach (var d in due)
            {
                await FinishAsync(d.Id, "dropped", "push_unavailable", now, ct);
            }
            return due.Count;
        }
        var tenantLocale = await tenantSettings.GetLocaleSettingsAsync(ct);
        foreach (var d in due)
        {
            if (await memberships.GetAsync(d.UserId, ct) is not { Status: MembershipStatus.Active })
            {
                await FinishAsync(d.Id, "dropped", "member_inactive", now, ct);
                continue;
            }
            var devices = await session.QueryAsync(
                "select id, endpoint, p256dh, auth from notifications.push_subscription where user_id = @userId",
                new { userId = d.UserId }, r => new Device(r.GetGuid(0), r.GetString(1), r.GetString(2), r.GetString(3)), ct);
            if (devices.Count == 0)
            {
                await FinishAsync(d.Id, "dropped", "no_device", now, ct);
                continue;
            }
            var user = await users.GetAsync(d.UserId, ct);
            var locale = LocaleResolver.Resolve(user?.PreferredLocale, tenantLocale.EnabledLocales, tenantLocale.DefaultLocale);
            var payload = Payload(d, locale);

            int sent = 0, transient = 0;
            foreach (var device in devices)
            {
                var outcome = await push.SendAsync(new WebPushTarget(device.Endpoint, device.P256dh, device.Auth), payload, TimeToLive, d.Urgent, ct);
                switch (outcome)
                {
                    case WebPushOutcome.Sent:
                        sent++;
                        break;
                    case WebPushOutcome.Gone:
                        await session.ExecAsync("delete from notifications.push_subscription where id = @id", new { id = device.Id }, ct);
                        await outbox.EnqueueAsync(new PushSubscriptionRemoved(d.UserId, "gone"), ct);
                        break;
                    case WebPushOutcome.Transient:
                        transient++;
                        break;
                    default:
                        logger.LogWarning("Push delivery {DeliveryId} for tenant {TenantId} was refused: {Outcome}", d.Id, context.RequireTenant(), outcome);
                        break;
                }
            }
            if (sent > 0)
            {
                await FinishAsync(d.Id, "sent", null, now, ct);
                await outbox.EnqueueAsync(new NotificationDelivered(d.NotificationId, d.UserId, NotificationChannels.Push), ct);
            }
            else if (transient > 0 && now - d.CreatedAt < RetryFor)
            {
                var tried = d.Attempts + 1;
                await session.ExecAsync(
                    "update notifications.delivery set attempts = @tried, not_before = @next, last_error = 'push_unavailable' where id = @id",
                    new { id = d.Id, tried, next = now + EmailDelivery.BackoffAfter(tried) }, ct);
            }
            else
            {
                await FinishAsync(d.Id, transient > 0 ? "failed" : "dropped", transient > 0 ? "push_failed" : "no_device", now, ct);
            }
        }
        return due.Count;
    }

    /// <summary>The message the device shows: the category and an invitation to open it, never the item's own words.</summary>
    private string Payload(Due d, string locale)
    {
        var label = d.Mandatory ? $"[{localizer.Get("notifications.email.mandatory", locale)}] " : "";
        var title = Clip(label + localizer.Get($"notifications.push.category.{d.Category}", locale), MaxTitle);
        var body = Clip(localizer.Get("notifications.push.body", locale), MaxBody);
        return JsonSerializer.Serialize(new { title, body, link = d.Link, tag = d.NotificationId.ToString("N") }, JsonDefaults.Options);
    }

    private static string Clip(string text, int max) => text.Length <= max ? text : text[..(max - 1)].TrimEnd() + "…";

    private Task FinishAsync(Guid id, string status, string? error, DateTimeOffset now, CancellationToken ct) =>
        session.ExecAsync(
            "update notifications.delivery set status = @status, last_error = @error, sent_at = case when @status = 'sent' then @now else sent_at end where id = @id",
            new { id, status, error, now }, ct);
}

public sealed class DropPushOnSessionEnded(PushSubscriptions subscriptions) : IMessageHandler<SessionEnded>
{
    public InactiveTenantPolicy InactiveTenantPolicy => InactiveTenantPolicy.Process;

    public Task HandleAsync(SessionEnded message, CancellationToken ct) => subscriptions.RemoveForSessionAsync(message.UserId, message.SessionId, "sign_out", ct);
}

public sealed class DropPushOnSessionRevoked(PushSubscriptions subscriptions) : IMessageHandler<SessionRevoked>
{
    public InactiveTenantPolicy InactiveTenantPolicy => InactiveTenantPolicy.Process;

    public Task HandleAsync(SessionRevoked message, CancellationToken ct) => subscriptions.RemoveForUserAsync(message.UserId, "session_revoked", ct);
}

public sealed class DropPushOnUserDeprovisioned(PushSubscriptions subscriptions) : IMessageHandler<UserDeprovisioned>
{
    public InactiveTenantPolicy InactiveTenantPolicy => InactiveTenantPolicy.Process;

    public Task HandleAsync(UserDeprovisioned message, CancellationToken ct) => subscriptions.RemoveForUserAsync(message.UserId, "membership_disabled", ct);
}
