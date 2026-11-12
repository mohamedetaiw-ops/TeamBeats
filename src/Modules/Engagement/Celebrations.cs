using TeamBeats.Audit.Contracts;
using TeamBeats.Authorization.Contracts;
using TeamBeats.Entitlements.Contracts;
using TeamBeats.Modules.Identity.Contracts;
using TeamBeats.Modules.Notifications.Contracts;
using TeamBeats.Modules.People.Contracts;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Primitives;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.Engagement;

public sealed record CelebrationEntry(Person Person, string Kind, DateOnly On, int? Years, bool Today, bool CanGreet);

public sealed record CelebrationsView(string Scope, IReadOnlyList<CelebrationEntry> Today, IReadOnlyList<CelebrationEntry> Upcoming, IReadOnlyList<CelebrationEntry> Joiners);

public sealed record CelebrationPreferenceRequest(int? BirthdayMonth, int? BirthdayDay, bool? AnniversaryOptOut);

public sealed record CelebrationPreferenceView(int? BirthdayMonth, int? BirthdayDay, bool AnniversaryOptOut, bool BirthdaysOn, bool AnniversariesOn);

public sealed record CelebrationSettingsRequest(bool? Birthdays, bool? Anniversaries, bool? Joiners);

public sealed record CelebrationSettingsView(bool Birthdays, bool Anniversaries, bool Joiners, int Version);

public sealed record GreetingRequest(string? Kind);

public static class CelebrationKinds
{
    public const string Birthday = "birthday";
    public const string Anniversary = "anniversary";
    public const string Joiner = "joiner";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal) { Birthday, Anniversary, Joiner };
}

/// <summary>
/// Celebrations (spec 023 US6, FR-015, FR-016). People who opt in give their birthday as a day and a month, never a
/// year; nothing about anyone else's birthday is stored or shown. Work anniversaries and new joiners come from people
/// (010) and appear as the tenant's settings allow; a person can leave their anniversary out. The list covers today
/// and the next six days in the tenant's time zone, for the viewer's org units and the units below them by default,
/// or the whole tenant. On the day, colleagues send a greeting, which reaches the person as a notification once per
/// colleague and occasion.
/// </summary>
public sealed class Celebrations(
    IDbSession session,
    ITenantContext context,
    IAuthorizationService authz,
    IEntitlementService entitlements,
    IOrganizationHierarchy hierarchy,
    IUserDirectory users,
    ICelebrationDates dates,
    INotificationSender notifications,
    Points points,
    IAuditWriter audit,
    IClock clock)
{
    public const int WindowDays = 7;
    public const string Path = "/communities/celebrations";

    public static string TemplateFor(string kind) => $"engagement.celebration.{kind}";

    private async Task RequireAsync(CancellationToken ct)
    {
        await entitlements.DemandAsync(Capabilities.EngagementCommunities, ct);
        await authz.DemandAsync(Permissions.EngagementParticipate, AuthzTarget.Tenant, ct);
    }

    // ---- Settings ----

    public static string SettingsETag(int version) => $"\"c{version}\"";

    private async Task<CelebrationSettingsView> SettingsAsync(CancellationToken ct) =>
        await session.QuerySingleOrDefaultAsync("select birthdays, anniversaries, joiners, version from engagement.celebration_setting", null,
            r => new CelebrationSettingsView(r.GetBoolean(0), r.GetBoolean(1), r.GetBoolean(2), r.GetInt32(3)), ct)
        // Defaults: birthdays on (each person still opts in), anniversaries off until the tenant turns them on, joiners on.
        ?? new CelebrationSettingsView(true, false, true, 0);

    public async Task<CelebrationSettingsView> GetSettingsAsync(CancellationToken ct)
    {
        await RequireAsync(ct);
        await authz.DemandAsync(Permissions.EngagementManage, AuthzTarget.Tenant, ct);
        return await SettingsAsync(ct);
    }

    public async Task<CelebrationSettingsView> SetSettingsAsync(CelebrationSettingsRequest request, string? ifMatch, CancellationToken ct)
    {
        await RequireAsync(ct);
        await authz.DemandAsync(Permissions.EngagementManage, AuthzTarget.Tenant, ct);
        var current = await SettingsAsync(ct);
        if (ifMatch != SettingsETag(current.Version))
        {
            throw new AppException(ProblemCodes.Conflict, 409, "conflict.engagement.celebrations_changed", null,
                new Dictionary<string, object?> { ["etag"] = SettingsETag(current.Version) });
        }
        if (request.Birthdays is not { } birthdays || request.Anniversaries is not { } anniversaries || request.Joiners is not { } joiners)
        {
            throw AppException.Validation("settings", "validation.engagement.celebration_settings");
        }
        var version = current.Version + 1;
        await session.ExecAsync(
            """
            insert into engagement.celebration_setting (tenant_id, birthdays, anniversaries, joiners, version, updated_by, updated_at)
            values (platform.current_tenant_id(), @birthdays, @anniversaries, @joiners, @version, @user, @now)
            on conflict (tenant_id) do update set birthdays = excluded.birthdays, anniversaries = excluded.anniversaries, joiners = excluded.joiners,
              version = excluded.version, updated_by = excluded.updated_by, updated_at = excluded.updated_at
            """, new { birthdays, anniversaries, joiners, version, user = context.RequireUser(), now = clock.UtcNow }, ct);
        await audit.WriteAsync(new AuditEntry(AuditActions.EngagementCelebrationSettingsChanged, "celebration_setting", null,
            Changes: new { version, birthdays, anniversaries, joiners }), ct);
        return new CelebrationSettingsView(birthdays, anniversaries, joiners, version);
    }

    // ---- Preferences ----

    private async Task<(int? Month, int? Day, bool AnniversaryOptOut)> PreferenceOfAsync(Guid user, CancellationToken ct)
    {
        var row = await session.QuerySingleOrDefaultAsync("select birthday_month, birthday_day, anniversary_opt_out from engagement.celebration_preference where user_id = @user",
            new { user }, r => (Month: r.Get<int?>("birthday_month"), Day: r.Get<int?>("birthday_day"), OptOut: r.GetBoolean(2)), ct);
        return row == default ? (null, null, false) : row;
    }

    public async Task<CelebrationPreferenceView> PreferenceAsync(CancellationToken ct)
    {
        await RequireAsync(ct);
        var (month, day, optOut) = await PreferenceOfAsync(context.RequireUser(), ct);
        var settings = await SettingsAsync(ct);
        return new CelebrationPreferenceView(month, day, optOut, settings.Birthdays, settings.Anniversaries);
    }

    /// <summary>Saves the person's choices. Leaving out the month and day withdraws the birthday and deletes it.</summary>
    public async Task<CelebrationPreferenceView> SetPreferenceAsync(CelebrationPreferenceRequest request, CancellationToken ct)
    {
        await RequireAsync(ct);
        var month = request.BirthdayMonth;
        var day = request.BirthdayDay;
        if ((month is null) != (day is null) || month is { } m && (m is < 1 or > 12 || day is not { } d || d < 1 || d > DateTime.DaysInMonth(2024, m)))
        {
            throw AppException.Validation("birthday", "validation.engagement.celebration_birthday");
        }
        var user = context.RequireUser();
        var optOut = request.AnniversaryOptOut ?? (await PreferenceOfAsync(user, ct)).AnniversaryOptOut;
        await session.ExecAsync(
            """
            insert into engagement.celebration_preference (tenant_id, user_id, birthday_month, birthday_day, anniversary_opt_out, updated_at)
            values (platform.current_tenant_id(), @user, @month, @day, @optOut, @now)
            on conflict (tenant_id, user_id) do update set birthday_month = excluded.birthday_month, birthday_day = excluded.birthday_day,
              anniversary_opt_out = excluded.anniversary_opt_out, updated_at = excluded.updated_at
            """, new { user, month, day, optOut, now = clock.UtcNow }, ct);
        var settings = await SettingsAsync(ct);
        return new CelebrationPreferenceView(month, day, optOut, settings.Birthdays, settings.Anniversaries);
    }

    // ---- The list ----

    /// <summary>Each day of the window with the month and day it celebrates; 29 February counts on the 28th in other years.</summary>
    internal static List<(DateOnly Date, int Month, int Day)> Window(DateOnly today, int days)
    {
        var result = new List<(DateOnly, int, int)>();
        for (var i = 0; i < days; i++)
        {
            var date = today.AddDays(i);
            result.Add((date, date.Month, date.Day));
            if (date.Month == 2 && date.Day == 28 && !DateTime.IsLeapYear(date.Year))
            {
                result.Add((date, 2, 29));
            }
        }
        return result;
    }

    private async Task<List<CelebrationEntry>> EntriesAsync(DateOnly today, CancellationToken ct)
    {
        var settings = await SettingsAsync(ct);
        var window = Window(today, WindowDays);
        var keys = window.Select(w => w.Month * 100 + w.Day).Distinct().ToArray();
        var found = new List<(Guid User, string Kind, DateOnly On, int? Years)>();
        if (settings.Birthdays)
        {
            var birthdays = await session.QueryAsync(
                "select user_id, birthday_month, birthday_day from engagement.celebration_preference where birthday_month is not null and birthday_month * 100 + birthday_day = any(@keys)",
                new { keys }, r => (User: r.GetGuid(0), Month: r.GetInt32(1), Day: r.GetInt32(2)), ct);
            foreach (var b in birthdays)
            {
                found.Add((b.User, CelebrationKinds.Birthday, window.First(w => w.Month == b.Month && w.Day == b.Day).Date, null));
            }
        }
        if (settings.Anniversaries)
        {
            var optedOut = (await session.QueryAsync("select user_id from engagement.celebration_preference where anniversary_opt_out", null, r => r.GetGuid(0), ct)).ToHashSet();
            foreach (var h in await dates.HireDatesOnAsync(window.Select(w => (w.Month, w.Day)).Distinct().ToList(), ct))
            {
                var on = window.First(w => w.Month == h.On.Month && w.Day == h.On.Day).Date;
                var years = on.Year - h.On.Year;
                if (years >= 1 && !optedOut.Contains(h.UserId))
                {
                    found.Add((h.UserId, CelebrationKinds.Anniversary, on, years));
                }
            }
        }
        if (settings.Joiners)
        {
            found.AddRange((await dates.JoinersAsync(ct)).Select(j => (j.UserId, CelebrationKinds.Joiner, j.On, (int?)null)));
        }
        var people = await users.GetManyAsync(found.Select(f => f.User).Distinct().ToList(), ct);
        var me = context.RequireUser();
        return found.Where(f => people.TryGetValue(f.User, out var u) && u.Status == "active")
            .Select(f =>
            {
                var isToday = f.Kind == CelebrationKinds.Joiner || f.On == today;
                return new CelebrationEntry(new Person(f.User, people[f.User].DisplayName, true), f.Kind, f.On, f.Years, isToday, isToday && f.User != me);
            }).ToList();
    }

    /// <summary>The people in the viewer's org units and the units below them; null when the viewer has no unit, which shows everyone.</summary>
    private async Task<IReadOnlySet<Guid>?> ColleaguesAsync(CancellationToken ct)
    {
        var mine = await hierarchy.GetActiveMembershipsAsync(context.RequireUser(), ct);
        if (mine.Count == 0)
        {
            return null;
        }
        return await hierarchy.GetMemberUserIdsAsync(await hierarchy.GetSubtreesAsync(mine.Select(m => m.OrgUnitId), ct), ct);
    }

    public async Task<CelebrationsView> ListAsync(string? scope, CancellationToken ct)
    {
        await RequireAsync(ct);
        scope ??= "unit";
        if (scope is not ("unit" or "all"))
        {
            throw AppException.Validation("scope", "validation.engagement.celebration_scope");
        }
        var today = await points.TodayAsync(ct);
        var entries = await EntriesAsync(today, ct);
        if (scope == "unit" && await ColleaguesAsync(ct) is { } colleagues)
        {
            entries = entries.Where(e => colleagues.Contains(e.Person.Id)).ToList();
        }
        var byName = StringComparer.CurrentCulture;
        return new CelebrationsView(scope,
            entries.Where(e => e.Kind != CelebrationKinds.Joiner && e.Today).OrderBy(e => e.Person.DisplayName, byName).ToList(),
            entries.Where(e => e.Kind != CelebrationKinds.Joiner && !e.Today).OrderBy(e => e.On).ThenBy(e => e.Person.DisplayName, byName).ToList(),
            entries.Where(e => e.Kind == CelebrationKinds.Joiner).OrderByDescending(e => e.On).ThenBy(e => e.Person.DisplayName, byName).Take(20).ToList());
    }

    /// <summary>A greeting on the day: the person hears once per colleague and occasion, however often it is sent.</summary>
    public async Task GreetAsync(Guid userId, GreetingRequest request, CancellationToken ct)
    {
        await RequireAsync(ct);
        var kind = request.Kind ?? "";
        if (!CelebrationKinds.All.Contains(kind))
        {
            throw AppException.Validation("kind", "validation.engagement.celebration_kind");
        }
        var today = await points.TodayAsync(ct);
        var entry = (await EntriesAsync(today, ct)).FirstOrDefault(e => e.Person.Id == userId && e.Kind == kind && e.CanGreet)
            ?? throw AppException.NotFound();
        var me = context.RequireUser();
        var name = (await users.GetManyAsync([me], ct)).TryGetValue(me, out var u) ? u.DisplayName : "";
        await notifications.SendAsync(new NotificationRequest([entry.Person.Id], NotificationCategories.Recognition, TemplateFor(kind), Path,
            new Dictionary<string, string> { ["sender"] = name }, DedupeKey: $"engagement.greeting:{kind}:{userId:N}:{me:N}:{today:yyyyMMdd}"), ct);
    }
}
