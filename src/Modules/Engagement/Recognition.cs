using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using TeamBeats.Audit.Contracts;
using TeamBeats.Authorization.Contracts;
using TeamBeats.Entitlements.Contracts;
using TeamBeats.Messaging;
using TeamBeats.Modules.Engagement.Contracts;
using TeamBeats.Modules.Identity.Contracts;
using TeamBeats.Modules.Notifications.Contracts;
using TeamBeats.Modules.People.Contracts;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Primitives;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.Engagement;

public sealed record RecognitionRequest(IReadOnlyList<Guid>? Recipients, Guid? TeamUnitId, string? Message, Guid? ValueId, Guid? BadgeId);

public sealed record RecognitionValueView(Guid Id, string Key, IReadOnlyDictionary<string, string> Names, bool Active);

public sealed record RecognitionBadgeView(Guid Id, string Key, IReadOnlyDictionary<string, string> Names, string Icon, Guid? GiverUnitId, bool Active);

public sealed record RecognitionTeam(Guid UnitId, IReadOnlyDictionary<string, string> Names);

public sealed record RecognitionView(Guid Id, Person Giver, IReadOnlyList<Person> Recipients, RecognitionTeam? Team, string Message, RecognitionValueView? Value,
    RecognitionBadgeView? Badge, DateTimeOffset CreatedAt, bool CanRemove);

public sealed record RecognitionPage(IReadOnlyList<RecognitionView> Items, string? NextCursor);

/// <summary>What the give form offers this person: active values, the badges they may give, and whether points show at all.</summary>
public sealed record RecognitionOptions(IReadOnlyList<RecognitionValueView> Values, IReadOnlyList<RecognitionBadgeView> Badges, bool PointsOn, bool Leaderboard, bool CanManage);

public sealed record RecognitionValueRequest(string? Key, Dictionary<string, string>? Names, bool Active = true);

public sealed record RecognitionBadgeRequest(string? Key, Dictionary<string, string>? Names, string? Icon, Guid? GiverUnitId, bool Active = true);

public sealed record RecognitionCatalogueRequest(IReadOnlyList<RecognitionValueRequest>? Values, IReadOnlyList<RecognitionBadgeRequest>? Badges);

public sealed record RecognitionCatalogue(IReadOnlyList<RecognitionValueView> Values, IReadOnlyList<RecognitionBadgeView> Badges);

public sealed record RecognitionPreferenceRequest(bool? ShowOnProfile, bool? LeaderboardOptOut);

public sealed record RecognitionPreferenceView(bool ShowOnProfile, bool LeaderboardOptOut);

/// <summary>
/// Recognition (spec 023 US4, FR-011). Anyone who takes part thanks people or a team, optionally naming a company value
/// and a badge they are allowed to give; nobody recognizes themselves. A shout-out shows on the recognition wall and,
/// where the person allows it, on their profile, and the people thanked and their managers are told. When points are
/// on, each person thanked earns the configured points, but only the giver's first shout-outs of the day (the tenant's
/// limit, default 5) count. A team shout-out earns no points. The giver or an engagement administrator removes a
/// shout-out, which takes its points back.
/// </summary>
public sealed partial class Recognition(
    IDbSession session,
    EngagementActivity activity,
    ITenantContext context,
    IAuthorizationService authz,
    IEntitlementService entitlements,
    IOrganizationHierarchy hierarchy,
    IUserDirectory users,
    IReportingLine reportingLine,
    ITenantSettingsReader tenantSettings,
    INotificationSender notifications,
    Points points,
    IAuditWriter audit,
    IOutbox outbox,
    IClock clock)
{
    public const int MaxRecipients = 20;
    public const int MaxMessage = 1000;
    public const int MaxValues = 30;
    public const int MaxBadges = 50;
    public const int MaxNameLength = 80;
    public const int PageSize = 20;
    public const string ReceivedTemplate = "engagement.recognition.received";
    public const string ReportTemplate = "engagement.recognition.report";
    public const string WallPath = "/communities/recognition";

    /// <summary>Icons a badge may use, from the design system's icon set.</summary>
    public static readonly IReadOnlySet<string> BadgeIcons = new HashSet<string>(StringComparer.Ordinal) { "star", "spark", "trend-up", "check", "megaphone", "people", "book", "globe", "lock", "clock" };

    [GeneratedRegex("^[a-z0-9][a-z0-9_-]{0,39}$")]
    private static partial Regex KeyPattern();

    private async Task RequireAsync(CancellationToken ct) => await entitlements.DemandAsync(Capabilities.EngagementRecognition, ct);

    private async Task<bool> ManagesAsync(CancellationToken ct) => (await authz.CheckAsync(Permissions.EngagementManage, AuthzTarget.Tenant, ct)).Allowed;

    private async Task<bool> InUnitAsync(Guid userId, Guid unitId, CancellationToken ct)
    {
        foreach (var m in await hierarchy.GetActiveMembershipsAsync(userId, ct))
        {
            if (await hierarchy.IsAncestorOrSelfAsync(unitId, m.OrgUnitId, ct))
            {
                return true;
            }
        }
        return false;
    }

    // ---- Catalogue ----

    private async Task<List<RecognitionValueView>> ValuesAsync(bool activeOnly, CancellationToken ct) =>
        (await session.QueryAsync($"select id, key, names::text, active from engagement.recognition_value {(activeOnly ? "where active" : "")} order by position, key", null,
            r => new RecognitionValueView(r.GetGuid(0), r.GetString(1), EngagementJson.Read<Dictionary<string, string>>(r.GetString(2)), r.GetBoolean(3)), ct)).ToList();

    private async Task<List<RecognitionBadgeView>> BadgesAsync(bool activeOnly, CancellationToken ct) =>
        (await session.QueryAsync($"select id, key, names::text, icon, giver_unit_id, active from engagement.recognition_badge {(activeOnly ? "where active" : "")} order by position, key", null,
            r => new RecognitionBadgeView(r.GetGuid(0), r.GetString(1), EngagementJson.Read<Dictionary<string, string>>(r.GetString(2)), r.GetString(3), r.Get<Guid?>("giver_unit_id"),
                r.GetBoolean(5)), ct)).ToList();

    /// <summary>The badges this person may give (US4 scenario 3): open badges, and those limited to a unit they belong to or sit below.</summary>
    private async Task<List<RecognitionBadgeView>> GivableBadgesAsync(Guid user, CancellationToken ct)
    {
        var givable = new List<RecognitionBadgeView>();
        foreach (var b in await BadgesAsync(true, ct))
        {
            if (b.GiverUnitId is not { } unit || await InUnitAsync(user, unit, ct))
            {
                givable.Add(b);
            }
        }
        return givable;
    }

    public async Task<RecognitionOptions> OptionsAsync(CancellationToken ct)
    {
        await RequireAsync(ct);
        var on = await points.OnAsync(ct);
        return new RecognitionOptions(await ValuesAsync(true, ct), await GivableBadgesAsync(context.RequireUser(), ct), on, on && await points.LeaderboardOnAsync(ct), await ManagesAsync(ct));
    }

    public async Task<RecognitionCatalogue> CatalogueAsync(CancellationToken ct)
    {
        await RequireAsync(ct);
        await authz.DemandAsync(Permissions.EngagementManage, AuthzTarget.Tenant, ct);
        return new RecognitionCatalogue(await ValuesAsync(false, ct), await BadgesAsync(false, ct));
    }

    /// <summary>
    /// Saves the values and badges as a whole. Entries are matched by key; an entry left out is retired, never deleted,
    /// so past shout-outs keep their value and badge.
    /// </summary>
    public async Task<RecognitionCatalogue> SetCatalogueAsync(RecognitionCatalogueRequest request, CancellationToken ct)
    {
        await RequireAsync(ct);
        await authz.DemandAsync(Permissions.EngagementManage, AuthzTarget.Tenant, ct);
        var enabled = (await tenantSettings.GetLocaleSettingsAsync(ct)).EnabledLocales;
        var errors = new ValidationErrors();
        bool NamesOk(Dictionary<string, string>? names) => names is { Count: > 0 } && names.Keys.All(enabled.Contains)
            && names.Values.All(n => !string.IsNullOrWhiteSpace(n) && n.Trim().Length <= MaxNameLength && !n.Any(char.IsControl));
        var values = request.Values ?? [];
        var badges = request.Badges ?? [];
        if (values.Count > MaxValues || values.Any(v => v.Key is null || !KeyPattern().IsMatch(v.Key) || !NamesOk(v.Names))
            || values.Select(v => v.Key).Distinct().Count() != values.Count)
        {
            errors.Add("values", "validation.engagement.recognition_values");
        }
        if (badges.Count > MaxBadges || badges.Any(b => b.Key is null || !KeyPattern().IsMatch(b.Key) || !NamesOk(b.Names) || b.Icon is null || !BadgeIcons.Contains(b.Icon))
            || badges.Select(b => b.Key).Distinct().Count() != badges.Count)
        {
            errors.Add("badges", "validation.engagement.recognition_badges");
        }
        foreach (var unit in badges.Select(b => b.GiverUnitId).OfType<Guid>().Distinct())
        {
            if (!await hierarchy.UnitExistsAsync(unit, ct))
            {
                errors.Add("badges", "validation.engagement.recognition_badge_unit");
            }
        }
        errors.ThrowIfAny();
        await session.ExecAsync("update engagement.recognition_value set active = false where not (key = any(@keys))", new { keys = values.Select(v => v.Key!).ToArray() }, ct);
        for (var i = 0; i < values.Count; i++)
        {
            var v = values[i];
            await session.ExecAsync(
                """
                insert into engagement.recognition_value (tenant_id, id, key, names, active, position) values (platform.current_tenant_id(), @id, @key, @names, @active, @i)
                on conflict (tenant_id, key) do update set names = excluded.names, active = excluded.active, position = excluded.position
                """, new { id = Ids.New(), key = v.Key, names = Trimmed(v.Names!), active = v.Active, i }, ct);
        }
        await session.ExecAsync("update engagement.recognition_badge set active = false where not (key = any(@keys))", new { keys = badges.Select(b => b.Key!).ToArray() }, ct);
        for (var i = 0; i < badges.Count; i++)
        {
            var b = badges[i];
            await session.ExecAsync(
                """
                insert into engagement.recognition_badge (tenant_id, id, key, names, icon, giver_unit_id, active, position)
                values (platform.current_tenant_id(), @id, @key, @names, @icon, @unit, @active, @i)
                on conflict (tenant_id, key) do update set names = excluded.names, icon = excluded.icon, giver_unit_id = excluded.giver_unit_id, active = excluded.active, position = excluded.position
                """, new { id = Ids.New(), key = b.Key, names = Trimmed(b.Names!), icon = b.Icon, unit = b.GiverUnitId, active = b.Active, i }, ct);
        }
        await audit.WriteAsync(new AuditEntry(AuditActions.EngagementRecognitionCatalogueChanged, "recognition_catalogue", null,
            Changes: new { values = values.Select(v => v.Key).ToArray(), badges = badges.Select(b => b.Key).ToArray() }), ct);
        return new RecognitionCatalogue(await ValuesAsync(false, ct), await BadgesAsync(false, ct));
    }

    private static Jsonb Trimmed(Dictionary<string, string> names) => new(JsonSerializer.Serialize(names.ToDictionary(n => n.Key, n => n.Value.Trim())));

    // ---- Giving ----

    public async Task<RecognitionView> GiveAsync(RecognitionRequest request, CancellationToken ct)
    {
        await RequireAsync(ct);
        await authz.DemandAsync(Permissions.EngagementParticipate, AuthzTarget.Tenant, ct);
        var giver = context.RequireUser();
        var errors = new ValidationErrors();
        var recipients = (request.Recipients ?? []).Distinct().ToList();
        if ((recipients.Count == 0) == (request.TeamUnitId is null) || recipients.Count > MaxRecipients)
        {
            errors.Add("recipients", "validation.engagement.recognition_recipients");
        }
        if (recipients.Contains(giver))
        {
            errors.Add("recipients", "validation.engagement.recognition_self");
        }
        if (recipients.Count > 0)
        {
            var found = await users.GetManyAsync(recipients, ct);
            if (recipients.Any(r => !found.TryGetValue(r, out var u) || u.Status != "active"))
            {
                errors.Add("recipients", "validation.engagement.recognition_recipient");
            }
        }
        if (request.TeamUnitId is { } team && !await hierarchy.UnitExistsAsync(team, ct))
        {
            errors.Add("teamUnitId", "validation.engagement.recognition_team");
        }
        var message = request.Message?.Trim() ?? "";
        if (message.Length is 0 or > MaxMessage)
        {
            errors.Add("message", "validation.engagement.recognition_message");
        }
        if (request.ValueId is { } value && !(await ValuesAsync(true, ct)).Any(v => v.Id == value))
        {
            errors.Add("valueId", "validation.engagement.recognition_value");
        }
        if (request.BadgeId is { } badge && !(await GivableBadgesAsync(giver, ct)).Any(b => b.Id == badge))
        {
            errors.Add("badgeId", "validation.engagement.recognition_badge");
        }
        errors.ThrowIfAny();

        // Only the giver's first shout-outs of the day earn points (US4 scenario 2); later ones still show.
        var counted = false;
        if (recipients.Count > 0 && await points.OnAsync(ct))
        {
            var today = await points.TodayAsync(ct);
            var zone = (await tenantSettings.GetLocaleSettingsAsync(ct)).TimeZone;
            var given = await session.ScalarAsync<long>(
                "select count(*) from engagement.recognition where giver_id = @giver and counted and (created_at at time zone @zone)::date = @today",
                new { giver, zone, today }, ct);
            counted = given < await points.GiverDailyLimitAsync(ct);
        }
        var id = Ids.New();
        var now = clock.UtcNow;
        await session.ExecAsync(
            """
            insert into engagement.recognition (tenant_id, id, giver_id, message, value_id, badge_id, team_unit_id, counted, created_at)
            values (platform.current_tenant_id(), @id, @giver, @message, @value, @badge, @team, @counted, @now)
            """, new { id, giver, message, value = request.ValueId, badge = request.BadgeId, team = request.TeamUnitId, counted, now }, ct);
        foreach (var r in recipients)
        {
            await session.ExecAsync("insert into engagement.recognition_recipient (tenant_id, recognition_id, user_id) values (platform.current_tenant_id(), @id, @r)", new { id, r }, ct);
            if (counted)
            {
                await points.AwardAsync(r, PointActions.RecognitionReceived, id, ct);
            }
        }
        await NotifyAsync(id, giver, recipients, ct);
        await outbox.EnqueueAsync(new RecognitionGiven(id, giver, recipients, request.TeamUnitId), ct);
        await activity.ActedAsync("recognize", null, ct);
        return (await ViewsAsync([id], ct)).Single();
    }

    /// <summary>The people thanked are told, and so are their managers (US4 scenario 1), never the giver about themselves.</summary>
    private async Task NotifyAsync(Guid id, Guid giver, IReadOnlyList<Guid> recipients, CancellationToken ct)
    {
        if (recipients.Count == 0)
        {
            return;
        }
        var name = (await users.GetManyAsync([giver], ct)).TryGetValue(giver, out var g) ? g.DisplayName : "";
        var link = $"{WallPath}#recognition-{id}";
        await notifications.SendAsync(new NotificationRequest(recipients, NotificationCategories.Recognition, ReceivedTemplate, link,
            new Dictionary<string, string> { ["giver"] = name }, DedupeKey: $"engagement.recognition:{id:N}"), ct);
        var managers = new HashSet<Guid>();
        foreach (var r in recipients)
        {
            if (await reportingLine.ManagerOfAsync(r, ct) is { } m && m != giver && !recipients.Contains(m))
            {
                managers.Add(m);
            }
        }
        if (managers.Count > 0)
        {
            await notifications.SendAsync(new NotificationRequest(managers, NotificationCategories.Recognition, ReportTemplate, link,
                new Dictionary<string, string> { ["giver"] = name }, DedupeKey: $"engagement.recognition.report:{id:N}"), ct);
        }
    }

    public async Task RemoveAsync(Guid id, CancellationToken ct)
    {
        await RequireAsync(ct);
        var giver = await session.QuerySingleOrDefaultAsync("select giver_id from engagement.recognition where id = @id and status = 'visible'", new { id }, r => (Guid?)r.GetGuid(0), ct)
            ?? throw AppException.NotFound();
        if (giver != context.RequireUser() && !await ManagesAsync(ct))
        {
            throw AppException.Denied();
        }
        await session.ExecAsync("update engagement.recognition set status = 'removed' where id = @id", new { id }, ct);
        await points.RevokeAsync(PointActions.RecognitionReceived, id, ct);
        if (giver != context.RequireUser())
        {
            await audit.WriteAsync(new AuditEntry(AuditActions.EngagementContentRemoved, "recognition", id.ToString(), Changes: new { giver }), ct);
        }
    }

    // ---- Reading ----

    private const string Columns = "id, giver_id, message, value_id, badge_id, team_unit_id, created_at";

    private async Task<List<RecognitionView>> ViewsAsync(IReadOnlyList<Guid> ids, CancellationToken ct)
    {
        if (ids.Count == 0)
        {
            return [];
        }
        var rows = await session.QueryAsync($"select {Columns} from engagement.recognition where id = any(@ids) and status = 'visible' order by created_at desc, id desc",
            new { ids = ids.ToArray() }, r => (Id: r.GetGuid(0), Giver: r.GetGuid(1), Message: r.GetString(2), Value: r.Get<Guid?>("value_id"), Badge: r.Get<Guid?>("badge_id"),
                Team: r.Get<Guid?>("team_unit_id"), At: r.GetFieldValue<DateTimeOffset>(6)), ct);
        var recipients = (await session.QueryAsync("select recognition_id, user_id from engagement.recognition_recipient where recognition_id = any(@ids)", new { ids = ids.ToArray() },
            r => (Id: r.GetGuid(0), User: r.GetGuid(1)), ct)).ToLookup(r => r.Id, r => r.User);
        var people = await users.GetManyAsync(rows.Select(r => r.Giver).Concat(recipients.SelectMany(r => r)).Distinct().ToList(), ct);
        Person P(Guid id) => people.TryGetValue(id, out var u) ? new Person(id, u.DisplayName, u.Status == "active") : new Person(id, "", false);
        var values = (await ValuesAsync(false, ct)).ToDictionary(v => v.Id);
        var badges = (await BadgesAsync(false, ct)).ToDictionary(b => b.Id);
        var teams = await hierarchy.GetNamesAsync(rows.Select(r => r.Team).OfType<Guid>().Distinct(), ct);
        var me = context.RequireUser();
        var manages = await ManagesAsync(ct);
        return rows.Select(r => new RecognitionView(r.Id, P(r.Giver), recipients[r.Id].Select(P).OrderBy(p => p.DisplayName, StringComparer.CurrentCulture).ToList(),
            r.Team is { } t ? new RecognitionTeam(t, teams.TryGetValue(t, out var n) ? n : new Dictionary<string, string>()) : null,
            r.Message, r.Value is { } v && values.TryGetValue(v, out var vv) ? vv : null, r.Badge is { } b && badges.TryGetValue(b, out var bb) ? bb : null, r.At,
            r.Giver == me || manages)).ToList();
    }

    private static (DateTimeOffset At, Guid Id)? Cursor(string? cursor)
    {
        if (string.IsNullOrEmpty(cursor))
        {
            return null;
        }
        var parts = cursor.Split('_');
        return parts.Length == 2 && long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var ticks) && Guid.TryParse(parts[1], out var id)
            ? (new DateTimeOffset(ticks, TimeSpan.Zero), id)
            : throw AppException.Validation("cursor", "validation.engagement.cursor");
    }

    /// <summary>The recognition wall: every visible shout-out in the tenant, newest first.</summary>
    public async Task<RecognitionPage> WallAsync(string? cursor, CancellationToken ct)
    {
        await RequireAsync(ct);
        await authz.DemandAsync(Permissions.EngagementParticipate, AuthzTarget.Tenant, ct);
        var after = Cursor(cursor);
        var ids = await session.QueryAsync(
            """
            select id, created_at from engagement.recognition where status = 'visible'
              and (@at::timestamptz is null or (created_at, id) < (@at, @id))
            order by created_at desc, id desc limit @limit
            """, new { at = after?.At, id = after?.Id ?? Guid.Empty, limit = PageSize + 1 }, r => (Id: r.GetGuid(0), At: r.GetFieldValue<DateTimeOffset>(1)), ct);
        var page = ids.Take(PageSize).ToList();
        var next = ids.Count > PageSize ? $"{page[^1].At.UtcTicks.ToString(CultureInfo.InvariantCulture)}_{page[^1].Id}" : null;
        return new RecognitionPage(await ViewsAsync(page.Select(p => p.Id).ToList(), ct), next);
    }

    /// <summary>
    /// Shout-outs a person received, for their profile (spec 010): shown only when they allow it, or to themselves.
    /// Someone who hides them shows the same empty list as someone with none.
    /// </summary>
    public async Task<IReadOnlyList<RecognitionView>> ReceivedAsync(Guid userId, CancellationToken ct)
    {
        await RequireAsync(ct);
        await authz.DemandAsync(Permissions.EngagementParticipate, AuthzTarget.Tenant, ct);
        if (userId != context.RequireUser() && !(await users.GetManyAsync([userId], ct)).ContainsKey(userId))
        {
            throw AppException.NotFound();
        }
        if (userId != context.RequireUser() && !(await PreferenceOfAsync(userId, ct)).ShowOnProfile)
        {
            return [];
        }
        var ids = await session.QueryAsync(
            """
            select r.id from engagement.recognition r join engagement.recognition_recipient p on p.tenant_id = r.tenant_id and p.recognition_id = r.id
            where p.user_id = @userId and r.status = 'visible' order by r.created_at desc limit 50
            """, new { userId }, r => r.GetGuid(0), ct);
        return await ViewsAsync(ids, ct);
    }

    // ---- Preferences ----

    private async Task<RecognitionPreferenceView> PreferenceOfAsync(Guid userId, CancellationToken ct) =>
        await session.QuerySingleOrDefaultAsync("select show_on_profile, leaderboard_opt_out from engagement.recognition_preference where user_id = @userId", new { userId },
            r => new RecognitionPreferenceView(r.GetBoolean(0), r.GetBoolean(1)), ct) ?? new RecognitionPreferenceView(true, false);

    public async Task<RecognitionPreferenceView> PreferenceAsync(CancellationToken ct)
    {
        await RequireAsync(ct);
        return await PreferenceOfAsync(context.RequireUser(), ct);
    }

    public async Task<RecognitionPreferenceView> SetPreferenceAsync(RecognitionPreferenceRequest request, CancellationToken ct)
    {
        await RequireAsync(ct);
        var user = context.RequireUser();
        var current = await PreferenceOfAsync(user, ct);
        var next = new RecognitionPreferenceView(request.ShowOnProfile ?? current.ShowOnProfile, request.LeaderboardOptOut ?? current.LeaderboardOptOut);
        await session.ExecAsync(
            """
            insert into engagement.recognition_preference (tenant_id, user_id, show_on_profile, leaderboard_opt_out, updated_at)
            values (platform.current_tenant_id(), @user, @show, @optOut, @now)
            on conflict (tenant_id, user_id) do update set show_on_profile = excluded.show_on_profile, leaderboard_opt_out = excluded.leaderboard_opt_out, updated_at = excluded.updated_at
            """, new { user, show = next.ShowOnProfile, optOut = next.LeaderboardOptOut, now = clock.UtcNow }, ct);
        return next;
    }
}
