using TeamBeats.Modules.Identity.Contracts;
using TeamBeats.Modules.Integrations.Contracts;
using TeamBeats.Modules.Notifications.Contracts;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Primitives;

namespace TeamBeats.Modules.Notifications;

public sealed record ChannelChoice(bool Email, bool Digest, bool Slack = false, bool Push = true);

/// <summary>
/// A category's channels. <paramref name="Slack"/> only has effect while the person's Slack account is linked, and
/// <paramref name="Push"/> only on devices where they allowed push (spec 030 US2).
/// </summary>
public sealed record CategoryPreference(string Category, bool Mandatory, bool Email, bool Digest, bool Slack, bool Push);

/// <summary>Slack for this person (spec 015 US8 scenario 3): available when the tenant has a Slack connection turned on.</summary>
public sealed record SlackPreference(bool Available, bool Linked);

public sealed record QuietHours(string Start, string End);

/// <summary>An employee's notification preferences (spec 015 US2, US3). In-app is always on.</summary>
public sealed record PreferencesView(IReadOnlyList<CategoryPreference> Categories, QuietHours? QuietHours, string DigestTime, string TimeZone, bool EmailPaused,
    SlackPreference Slack, PushStatus Push);

/// <summary>A category's new channels. <paramref name="Slack"/> or <paramref name="Push"/> left out keeps the current choice.</summary>
public sealed record PreferenceChange(string Category, bool Email, bool Digest, bool? Slack = null, bool? Push = null);

public sealed record UpdatePreferencesRequest(IReadOnlyList<PreferenceChange>? Categories, QuietHours? QuietHours, string? DigestTime, string? TimeZone);

/// <summary>Channel preferences, daily digest and quiet hours per user. Mandatory categories ignore preferences (FR-005).</summary>
public sealed class PreferenceService(IDbSession session, IClock clock, NotificationSettingsService settings, ITenantSettingsReader tenantSettings, IUserDirectory users,
    ISlackMessaging slack, PushSubscriptions push)
{
    private sealed record UserSettings(TimeOnly? QuietStart, TimeOnly? QuietEnd, string? TimeZone, TimeOnly DigestTime, DateTimeOffset? LastDigestAt);

    public async Task<PreferencesView> GetAsync(Guid userId, CancellationToken ct)
    {
        var effective = await settings.GetEffectiveAsync(ct);
        var rows = await ReadPreferencesAsync(userId, ct);
        var categories = NotificationCategories.All.Select(c =>
        {
            var mandatory = effective.IsMandatory(c.Key);
            if (mandatory)
            {
                return new CategoryPreference(c.Key, true, true, false, true, true);
            }
            var row = rows.GetValueOrDefault(c.Key);
            return new CategoryPreference(c.Key, false, row?.Email ?? effective.EmailByDefault(c), row?.Digest ?? false, row?.Slack ?? false, row?.Push ?? true);
        }).ToList();
        var user = await ReadUserSettingsAsync(userId, ct);
        var quiet = user?.QuietStart is { } s && user.QuietEnd is { } e ? new QuietHours(s.ToString("HH:mm"), e.ToString("HH:mm")) : null;
        var email = (await users.GetAsync(userId, ct))?.Email?.Trim();
        var paused = email is not null && await session.ScalarAsync<bool>(
            "select exists (select 1 from notifications.address_state where address = @email)", new { email }, ct);
        var slackStatus = await slack.StatusAsync(userId, ct);
        return new PreferencesView(categories, quiet, (user?.DigestTime ?? new TimeOnly(8, 0)).ToString("HH:mm"), await TimeZoneAsync(user, ct), paused,
            new SlackPreference(slackStatus.Available, slackStatus.Available && slackStatus.Linked), await push.StatusAsync(userId, ct));
    }

    public async Task<PreferencesView> UpdateAsync(Guid userId, UpdatePreferencesRequest request, CancellationToken ct)
    {
        var errors = new ValidationErrors();
        var effective = await settings.GetEffectiveAsync(ct);
        foreach (var change in request.Categories ?? [])
        {
            if (NotificationCategories.Find(change.Category) is null)
            {
                errors.Add("categories", "validation.notification.category_unknown");
            }
        }
        TimeOnly? quietStart = null, quietEnd = null;
        if (request.QuietHours is { } q)
        {
            if (!TimeOnly.TryParseExact(q.Start, "HH:mm", out var qs) || !TimeOnly.TryParseExact(q.End, "HH:mm", out var qe) || qs == qe)
            {
                errors.Add("quietHours", "validation.notification.quiet_hours_invalid");
            }
            else
            {
                (quietStart, quietEnd) = (qs, qe);
            }
        }
        var digestTime = new TimeOnly(8, 0);
        if (request.DigestTime is { } d && !TimeOnly.TryParseExact(d, "HH:mm", out digestTime))
        {
            errors.Add("digestTime", "validation.notification.digest_time_invalid");
        }
        if (request.TimeZone is { } tz && !TryZone(tz, out _))
        {
            errors.Add("timeZone", "validation.notification.time_zone_invalid");
        }
        errors.ThrowIfAny();

        var now = clock.UtcNow;
        foreach (var change in request.Categories ?? [])
        {
            if (effective.IsMandatory(change.Category))
            {
                continue; // Mandatory categories cannot be turned off; the request is accepted and the row ignored.
            }
            await session.ExecAsync(
                """
                insert into notifications.preference (tenant_id, user_id, category, email, digest, slack, push, updated_at)
                values (platform.current_tenant_id(), @userId, @category, @email, @digest, coalesce(@slack, false), coalesce(@push, true), @now)
                on conflict (tenant_id, user_id, category) do update
                  set email = excluded.email, digest = excluded.digest, slack = coalesce(@slack, notifications.preference.slack),
                      push = coalesce(@push, notifications.preference.push), updated_at = excluded.updated_at
                """,
                new { userId, category = change.Category, email = change.Email, digest = change.Email && change.Digest, slack = change.Slack, push = change.Push, now }, ct);
        }

        await session.ExecAsync(
            """
            insert into notifications.user_settings (tenant_id, user_id, quiet_start, quiet_end, time_zone, digest_time, updated_at)
            values (platform.current_tenant_id(), @userId, @quietStart::time, @quietEnd::time, @timeZone, @digestTime::time, @now)
            on conflict (tenant_id, user_id) do update
              set quiet_start = excluded.quiet_start, quiet_end = excluded.quiet_end, time_zone = excluded.time_zone,
                  digest_time = excluded.digest_time, updated_at = excluded.updated_at
            """,
            new
            {
                userId, quietStart = quietStart?.ToString("HH:mm"), quietEnd = quietEnd?.ToString("HH:mm"), timeZone = request.TimeZone,
                digestTime = digestTime.ToString("HH:mm"), now,
            }, ct);
        return await GetAsync(userId, ct);
    }

    /// <summary>Links the person's Slack account by their work email (spec 015 US8 scenario 3). Throws when the workspace has no such member.</summary>
    public async Task<PreferencesView> LinkSlackAsync(Guid userId, CancellationToken ct)
    {
        var email = (await users.GetAsync(userId, ct))?.Email?.Trim();
        if (string.IsNullOrEmpty(email))
        {
            throw AppException.Validation("email", "validation.slack.no_email");
        }
        await slack.LinkAsync(userId, email, ct);
        return await GetAsync(userId, ct);
    }

    public async Task<PreferencesView> UnlinkSlackAsync(Guid userId, CancellationToken ct)
    {
        await slack.UnlinkAsync(userId, ct);
        return await GetAsync(userId, ct);
    }

    public async Task<ChannelChoice> ChannelChoiceAsync(Guid userId, string category, bool mandatory, CancellationToken ct)
    {
        if (mandatory)
        {
            return new ChannelChoice(true, false, true, true);
        }
        var row = await session.QuerySingleOrDefaultAsync(
            "select email, digest, slack, push from notifications.preference where user_id = @userId and category = @category",
            new { userId, category }, r => new ChannelChoice(r.GetBoolean(0), r.GetBoolean(1), r.GetBoolean(2), r.GetBoolean(3)), ct);
        if (row is not null)
        {
            return row;
        }
        var effective = await settings.GetEffectiveAsync(ct);
        return new ChannelChoice(effective.EmailByDefault(NotificationCategories.Find(category)!), false);
    }

    /// <summary>When a delivery may go out: now, or the end of the user's quiet hours if now falls inside them (FR-014).</summary>
    public async Task<DateTimeOffset> EndOfQuietHoursAsync(Guid userId, DateTimeOffset now, CancellationToken ct)
    {
        var user = await ReadUserSettingsAsync(userId, ct);
        if (user?.QuietStart is not { } start || user.QuietEnd is not { } end)
        {
            return now;
        }
        TryZone(await TimeZoneAsync(user, ct), out var zone);
        return QuietHoursWindow.ReleaseAt(now, start, end, zone);
    }

    /// <summary>The user's time zone and digest time, for the digest job.</summary>
    public async Task<(TimeZoneInfo Zone, TimeOnly DigestTime, DateTimeOffset? LastDigestAt)> DigestScheduleAsync(Guid userId, CancellationToken ct)
    {
        var user = await ReadUserSettingsAsync(userId, ct);
        TryZone(await TimeZoneAsync(user, ct), out var zone);
        return (zone, user?.DigestTime ?? new TimeOnly(8, 0), user?.LastDigestAt);
    }

    private async Task<string> TimeZoneAsync(UserSettings? user, CancellationToken ct) =>
        user?.TimeZone ?? (await tenantSettings.GetLocaleSettingsAsync(ct)).TimeZone;

    private async Task<Dictionary<string, ChannelChoice>> ReadPreferencesAsync(Guid userId, CancellationToken ct) =>
        (await session.QueryAsync(
            "select category, email, digest, slack, push from notifications.preference where user_id = @userId",
            new { userId }, r => (Category: r.GetString(0), Choice: new ChannelChoice(r.GetBoolean(1), r.GetBoolean(2), r.GetBoolean(3), r.GetBoolean(4))), ct))
        .ToDictionary(x => x.Category, x => x.Choice);

    private Task<UserSettings?> ReadUserSettingsAsync(Guid userId, CancellationToken ct) =>
        session.QuerySingleOrDefaultAsync(
            "select quiet_start, quiet_end, time_zone, digest_time, last_digest_at from notifications.user_settings where user_id = @userId",
            new { userId },
            r => new UserSettings(
                r.IsDBNull(0) ? null : TimeOnly.FromTimeSpan(r.GetTimeSpan(0)),
                r.IsDBNull(1) ? null : TimeOnly.FromTimeSpan(r.GetTimeSpan(1)),
                r.Get<string>("time_zone"),
                TimeOnly.FromTimeSpan(r.GetTimeSpan(3)),
                r.Get<DateTimeOffset?>("last_digest_at")),
            ct);

    public static bool TryZone(string id, out TimeZoneInfo zone)
    {
        try
        {
            zone = TimeZoneInfo.FindSystemTimeZoneById(id);
            return true;
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            zone = TimeZoneInfo.Utc;
            return false;
        }
    }
}

/// <summary>Quiet hours arithmetic in the user's time zone, including windows that run past midnight.</summary>
public static class QuietHoursWindow
{
    public static bool Contains(TimeOnly local, TimeOnly start, TimeOnly end) =>
        start < end ? local >= start && local < end : local >= start || local < end;

    public static DateTimeOffset ReleaseAt(DateTimeOffset now, TimeOnly start, TimeOnly end, TimeZoneInfo zone)
    {
        var local = TimeZoneInfo.ConvertTime(now, zone);
        if (!Contains(TimeOnly.FromDateTime(local.DateTime), start, end))
        {
            return now;
        }
        var day = DateOnly.FromDateTime(local.DateTime);
        if (TimeOnly.FromDateTime(local.DateTime) >= end)
        {
            day = day.AddDays(1); // Inside a window that started this evening: it ends tomorrow morning.
        }
        var releaseLocal = day.ToDateTime(end);
        return new DateTimeOffset(releaseLocal, zone.GetUtcOffset(releaseLocal)).ToUniversalTime();
    }
}
