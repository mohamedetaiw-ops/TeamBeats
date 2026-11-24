using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TeamBeats.Audit.Contracts;
using TeamBeats.Localization;
using TeamBeats.Modules.Identity.Contracts;
using TeamBeats.Modules.Integrations.Contracts;
using TeamBeats.Modules.Notifications.Contracts;
using TeamBeats.Modules.Security.Contracts;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Primitives;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.Notifications;

/// <summary>
/// Quick actions from Teams (spec 015 US5 scenario 2, FR-018, section 14). Each button of a Teams card carries its own
/// random token, bound to the person and the notification, good for 7 days and for one use; only its SHA-256 hash is
/// kept. The tenant is named in the token, so the bot endpoint can open the tenant before it looks the token up. A
/// press runs only when the Teams caller is that person (the tenant's Microsoft 365 directory and their linked Entra
/// object ID), they are still an active member, and the tenant's sign-in rules can be met from outside TeamBeats. The
/// owning module's handler then runs the action as the person, with its own authorization and checks. Anything that
/// needs a fresh sign-in, a trusted network or MFA is finished in TeamBeats instead.
/// </summary>
public sealed class QuickActions(
    IDbSession session,
    TenantContextAccessor accessor,
    IClock clock,
    IEnumerable<INotificationActionHandler> handlers,
    ITeamsMessaging teams,
    ITenantMemberships memberships,
    IUserDirectory users,
    ITenantSettingsReader tenantSettings,
    ISecurityPolicy securityPolicy,
    ILocalizer localizer,
    IAuditWriter audit)
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromDays(7);

    public const string ReasonLabelKey = "notifications.teams.reason";
    public const string OpenLabelKey = "notifications.teams.open";

    private sealed record Pending(Guid NotificationId, Guid UserId, string Action, DateTimeOffset ExpiresAt, DateTimeOffset? UsedAt, string? Actions, string Link,
        DateTimeOffset? ReadAt);

    public INotificationActionHandler? Handler(string name) => handlers.FirstOrDefault(h => h.Handler == name);

    /// <summary>The card buttons for one notification and person: one fresh token per action.</summary>
    public async Task<IReadOnlyList<TeamsCardAction>> MintAsync(Guid notificationId, Guid userId, NotificationActions offered, string locale, CancellationToken ct)
    {
        var tenant = accessor.RequireTenant();
        if (Handler(offered.Handler) is not { } handler)
        {
            return [];
        }
        var now = clock.UtcNow;
        var buttons = new List<TeamsCardAction>();
        foreach (var action in offered.Actions)
        {
            if (handler.LabelKey(action) is not { } label)
            {
                continue;
            }
            var secret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
            var token = $"{tenant:N}.{secret}";
            await session.ExecAsync(
                """
                insert into notifications.quick_action (tenant_id, token_hash, notification_id, user_id, action, expires_at)
                values (platform.current_tenant_id(), @hash, @notificationId, @userId, @action, @expires)
                """, new { hash = Hash(token), notificationId, userId, action, expires = now + Lifetime }, ct);
            buttons.Add(new TeamsCardAction(action, localizer.Get(label, locale), token, handler.NeedsReason(action)));
        }
        return buttons;
    }

    /// <summary>The tenant a token names, or null when it is not one of ours.</summary>
    public static Guid? TenantOf(string token)
    {
        var dot = token.IndexOf('.');
        return dot == 32 && token.Length is > 33 and <= 120 && Guid.TryParseExact(token[..dot], "N", out var tenant) ? tenant : null;
    }

    private static string Hash(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))).ToLowerInvariant();

    /// <summary>
    /// Runs a pressed action and says what happened, in the person's language. The tenant must already be in context
    /// (from <see cref="TenantOf"/>); the caller commits.
    /// </summary>
    public async Task<TeamsCardReply> RunAsync(TeamsBotAction pressed, CancellationToken ct)
    {
        var tenantLocale = await tenantSettings.GetLocaleSettingsAsync(ct);
        var pending = (await session.QueryAsync(
            """
            select q.notification_id, q.user_id, q.action, q.expires_at, q.used_at, n.actions::text, n.link, n.read_at
            from notifications.quick_action q
            join notifications.notification n on n.tenant_id = q.tenant_id and n.id = q.notification_id
            where q.token_hash = @hash
            for update of q
            """, new { hash = Hash(pressed.Token) },
            r => new Pending(r.GetGuid(0), r.GetGuid(1), r.GetString(2), r.GetFieldValue<DateTimeOffset>(3), r.IsDBNull(4) ? null : r.GetFieldValue<DateTimeOffset>(4),
                r.IsDBNull(5) ? null : r.GetString(5), r.GetString(6), r.IsDBNull(7) ? null : r.GetFieldValue<DateTimeOffset>(7)), ct)).SingleOrDefault();

        // Not ours, or the Teams caller is someone else: say nothing about the notification.
        if (pending is null || pending.Action != pressed.Verb || !await teams.IsPersonAsync(pending.UserId, pressed.EntraTenantId, pressed.AadObjectId, ct))
        {
            return Reply("notifications.teams.quick.expired", tenantLocale.DefaultLocale, null);
        }
        var user = await users.GetAsync(pending.UserId, ct);
        var locale = LocaleResolver.Resolve(user?.PreferredLocale, tenantLocale.EnabledLocales, tenantLocale.DefaultLocale);
        if (await memberships.GetAsync(pending.UserId, ct) is not { Status: MembershipStatus.Active })
        {
            return Reply("notifications.teams.quick.expired", locale, null);
        }
        if (pending.UsedAt is not null || pending.ExpiresAt <= clock.UtcNow || pending.Actions is null
            || JsonSerializer.Deserialize<NotificationActions>(pending.Actions, JsonDefaults.Options) is not { } offered
            || !offered.Actions.Contains(pending.Action) || Handler(offered.Handler) is not { } handler)
        {
            return Reply("notifications.teams.quick.expired", locale, pending.Link);
        }

        accessor.SetActor(ActorType.User, pending.UserId);
        if (handler.RequiresStepUp(pending.Action) || await NeedsTeamBeatsAsync(ct))
        {
            await AuditAsync(pending, offered, "step_up", ct);
            return Reply("notifications.teams.quick.step_up", locale, pending.Link);
        }
        if (handler.NeedsReason(pending.Action) && pressed.Reason is null)
        {
            return Reply("notifications.teams.quick.reason_required", locale, pending.Link);
        }
        QuickActionOutcome outcome;
        try
        {
            outcome = await handler.RunAsync(offered.Target, pending.Action, pressed.Reason, ct);
        }
        catch (AppException ex) when (ex.Status == 403)
        {
            outcome = QuickActionOutcome.Denied;
        }
        catch (AppException ex) when (ex.Status is 404 or 409)
        {
            outcome = QuickActionOutcome.Closed;
        }
        catch (AppException ex) when (ex.Status == 400)
        {
            outcome = QuickActionOutcome.ReasonRequired;
        }
        if (outcome is QuickActionOutcome.ReasonRequired)
        {
            return Reply("notifications.teams.quick.reason_required", locale, pending.Link);
        }
        // Every button of this card is spent once one of them was answered, whatever the answer.
        await session.ExecAsync(
            "update notifications.quick_action set used_at = @now where notification_id = @id and user_id = @user and used_at is null",
            new { now = clock.UtcNow, id = pending.NotificationId, user = pending.UserId }, ct);
        await AuditAsync(pending, offered, outcome switch
        {
            QuickActionOutcome.Done => "done",
            QuickActionOutcome.StepUpRequired => "step_up",
            QuickActionOutcome.Closed => "closed",
            _ => "denied",
        }, ct);
        return outcome switch
        {
            QuickActionOutcome.Done => Reply("notifications.teams.quick.done", locale, pending.Link),
            QuickActionOutcome.StepUpRequired => Reply("notifications.teams.quick.step_up", locale, pending.Link),
            QuickActionOutcome.Closed => Reply("notifications.teams.quick.closed", locale, pending.Link),
            _ => Reply("notifications.teams.quick.denied", locale, pending.Link),
        };
    }

    /// <summary>
    /// Teams cannot show that a press comes from a trusted network, with MFA, or within the step-up window, so a tenant
    /// that requires any of these finishes every quick action in TeamBeats.
    /// </summary>
    private async Task<bool> NeedsTeamBeatsAsync(CancellationToken ct)
    {
        var policy = await securityPolicy.GetAsync(ct);
        return policy.RequireMfa || policy.IpAllow.Count > 0 || policy.NetworkZones.Count > 0;
    }

    private Task AuditAsync(Pending pending, NotificationActions offered, string result, CancellationToken ct) =>
        audit.WriteAsync(new AuditEntry(AuditActions.NotificationQuickActionUsed, "notification", pending.NotificationId.ToString(),
            Changes: new { channel = NotificationChannels.Teams, handler = offered.Handler, action = pending.Action, result }), ct);

    private TeamsCardReply Reply(string key, string locale, string? link) =>
        new(localizer.Get(key, locale), link, link is null ? null : localizer.Get(OpenLabelKey, locale), Locales.IsRightToLeft(locale));
}
