using TeamBeats.Audit.Contracts;
using TeamBeats.Authorization.Contracts;
using TeamBeats.Entitlements.Contracts;
using TeamBeats.Localization;
using TeamBeats.Modules.Content.Contracts;
using TeamBeats.Modules.Identity.Contracts;
using TeamBeats.Modules.Notifications.Contracts;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Primitives;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.Engagement;

public sealed record AwardPeriodRequest(string? Kind, string? Title, Guid? OrgUnitId, DateOnly? NominationsCloseOn);

public sealed record AwardNominationRequest(Guid? NomineeId, string? Reason);

public sealed record AwardWinnersRequest(IReadOnlyList<Guid>? NomineeIds);

/// <summary>A nominee: a person for a person award, an org unit for a team award.</summary>
public sealed record AwardNominee(Guid Id, Person? Person, IReadOnlyDictionary<string, string>? UnitNames);

public sealed record AwardMyNomination(AwardNominee Nominee, string Reason);

public sealed record AwardPeriodView(
    Guid Id,
    string Kind,
    string Title,
    Guid? OrgUnitId,
    IReadOnlyDictionary<string, string>? UnitNames,
    DateOnly NominationsCloseOn,
    string Status,
    bool NominationsOpen,
    AwardMyNomination? MyNomination,
    IReadOnlyList<AwardNominee> Winners,
    bool CanDecide,
    Guid? AnnouncementItemId,
    DateTimeOffset CreatedAt);

public sealed record AwardList(bool CanCreate, IReadOnlyList<AwardPeriodView> Periods);

public sealed record AwardReason(Person Nominator, string Reason, DateTimeOffset At);

/// <summary>Everyone nominated in a period with the reasons given, most nominations first; only approvers see it.</summary>
public sealed record AwardNomineeSummary(AwardNominee Nominee, int Count, IReadOnlyList<AwardReason> Reasons);

public static class AwardKinds
{
    public const string Person = "person";
    public const string Team = "team";
}

public static class AwardStatuses
{
    public const string Open = "open";
    public const string Decided = "decided";
}

/// <summary>
/// Team and star of the period (spec 023 US5, FR-014). An engagement administrator opens a period for a person award or
/// a team award, for the whole tenant or an org unit and everything below it, with the last day for nominations.
/// Anyone who takes part nominates one person (never themselves) or one team in scope, with a reason, and may change
/// it until nominations close. Recognition approvers with <c>engagement.recognition_approve</c> on the period's unit
/// (or the tenant) see the nominations and pick up to five winners from them; that ends the period, tells the winning
/// people and starts an announcement draft in content (009) for communicators to finish and publish.
/// </summary>
public sealed class Awards(
    IDbSession session,
    ITenantContext context,
    IAuthorizationService authz,
    IEntitlementService entitlements,
    IOrganizationHierarchy hierarchy,
    IUserDirectory users,
    ITenantSettingsReader tenantSettings,
    IAnnouncementDrafts announcements,
    ILocalizer localizer,
    INotificationSender notifications,
    Points points,
    IAuditWriter audit,
    IClock clock)
{
    public const int MaxTitle = 200;
    public const int MaxReason = 1000;
    public const int MaxWinners = 5;
    public const int MaxOpenDays = 366;
    public const string WonTemplate = "engagement.award.won";
    public const string AwardsPath = "/communities/awards";

    private sealed record PeriodRow(Guid Id, string Kind, string Title, Guid? OrgUnitId, DateOnly CloseOn, string Status, Guid? AnnouncementItemId, DateTimeOffset CreatedAt);

    private const string Columns = "id, kind, title, org_unit_id, nominations_close_on, status, announcement_item_id, created_at";

    private static PeriodRow Read(Npgsql.NpgsqlDataReader r) => new(r.GetGuid(0), r.GetString(1), r.GetString(2), r.Get<Guid?>("org_unit_id"),
        r.GetFieldValue<DateOnly>(4), r.GetString(5), r.Get<Guid?>("announcement_item_id"), r.GetFieldValue<DateTimeOffset>(7));

    private async Task RequireAsync(CancellationToken ct)
    {
        await entitlements.DemandAsync(Capabilities.EngagementRecognition, ct);
        await authz.DemandAsync(Permissions.EngagementParticipate, AuthzTarget.Tenant, ct);
    }

    private static AuthzTarget Scope(Guid? unit) => unit is { } u ? AuthzTarget.OrgUnit(u) : AuthzTarget.Tenant;

    private async Task<bool> DecidesAsync(PeriodRow row, CancellationToken ct) =>
        (await authz.CheckAsync(Permissions.EngagementRecognitionApprove, Scope(row.OrgUnitId), ct)).Allowed;

    private async Task<bool> ManagesAsync(CancellationToken ct) => (await authz.CheckAsync(Permissions.EngagementManage, AuthzTarget.Tenant, ct)).Allowed;

    private async Task<PeriodRow?> RowAsync(Guid id, bool forUpdate, CancellationToken ct) =>
        await session.QuerySingleOrDefaultAsync($"select {Columns} from engagement.award_period where id = @id{(forUpdate ? " for update" : "")}", new { id }, Read, ct);

    // ---- Opening a period ----

    public async Task<AwardPeriodView> OpenAsync(AwardPeriodRequest request, CancellationToken ct)
    {
        await RequireAsync(ct);
        await authz.DemandAsync(Permissions.EngagementManage, AuthzTarget.Tenant, ct);
        var errors = new ValidationErrors();
        var kind = request.Kind ?? "";
        if (kind is not (AwardKinds.Person or AwardKinds.Team))
        {
            errors.Add("kind", "validation.engagement.award_kind");
        }
        var title = request.Title?.Trim() ?? "";
        if (title.Length is 0 or > MaxTitle)
        {
            errors.Add("title", "validation.engagement.award_title");
        }
        if (request.OrgUnitId is { } unit && !await hierarchy.UnitExistsAsync(unit, ct))
        {
            errors.Add("orgUnitId", "validation.engagement.award_unit");
        }
        var today = await points.TodayAsync(ct);
        if (request.NominationsCloseOn is not { } closeOn || closeOn < today || closeOn > today.AddDays(MaxOpenDays))
        {
            errors.Add("nominationsCloseOn", "validation.engagement.award_close_on");
            closeOn = today;
        }
        errors.ThrowIfAny();
        var id = Ids.New();
        await session.ExecAsync(
            """
            insert into engagement.award_period (tenant_id, id, kind, title, org_unit_id, nominations_close_on, created_by, created_at)
            values (platform.current_tenant_id(), @id, @kind, @title, @unit, @closeOn, @user, @now)
            """, new { id, kind, title, unit = request.OrgUnitId, closeOn, user = context.RequireUser(), now = clock.UtcNow }, ct);
        await audit.WriteAsync(new AuditEntry(AuditActions.EngagementAwardOpened, "award_period", id.ToString(), TargetOrgUnitId: request.OrgUnitId,
            Changes: new { kind, closeOn }), ct);
        return (await ViewsAsync([(await RowAsync(id, false, ct))!], ct)).Single();
    }

    // ---- Nominating ----

    /// <summary>Whether the nominee fits the period: an active person, or an existing unit, inside the period's scope.</summary>
    private async Task<bool> InScopeAsync(PeriodRow row, Guid nominee, CancellationToken ct)
    {
        IReadOnlySet<Guid>? subtree = row.OrgUnitId is { } unit ? await hierarchy.GetSubtreesAsync([unit], ct) : null;
        if (row.Kind == AwardKinds.Team)
        {
            return subtree?.Contains(nominee) ?? await hierarchy.UnitExistsAsync(nominee, ct);
        }
        if (!(await users.GetManyAsync([nominee], ct)).TryGetValue(nominee, out var person) || person.Status != "active")
        {
            return false;
        }
        return subtree is null || (await hierarchy.GetActiveMembershipsAsync(nominee, ct)).Any(m => subtree.Contains(m.OrgUnitId));
    }

    private async Task<bool> NominationsOpenAsync(PeriodRow row, CancellationToken ct) => row.Status == AwardStatuses.Open && await points.TodayAsync(ct) <= row.CloseOn;

    public async Task<AwardPeriodView> NominateAsync(Guid id, AwardNominationRequest request, CancellationToken ct)
    {
        await RequireAsync(ct);
        var row = await RowAsync(id, true, ct) ?? throw AppException.NotFound();
        if (!await NominationsOpenAsync(row, ct))
        {
            throw AppException.Conflict("conflict.engagement.award_closed");
        }
        var me = context.RequireUser();
        var errors = new ValidationErrors();
        if (request.NomineeId is not { } nominee || nominee == me || !await InScopeAsync(row, nominee, ct))
        {
            errors.Add("nomineeId", request.NomineeId == me ? "validation.engagement.award_self" : "validation.engagement.award_nominee");
            nominee = Guid.Empty;
        }
        var reason = request.Reason?.Trim() ?? "";
        if (reason.Length is 0 or > MaxReason)
        {
            errors.Add("reason", "validation.engagement.award_reason");
        }
        errors.ThrowIfAny();
        await session.ExecAsync(
            """
            insert into engagement.award_nomination (tenant_id, period_id, nominator_id, nominee_id, reason, created_at)
            values (platform.current_tenant_id(), @id, @me, @nominee, @reason, @now)
            on conflict (tenant_id, period_id, nominator_id) do update set nominee_id = excluded.nominee_id, reason = excluded.reason, created_at = excluded.created_at
            """, new { id, me, nominee, reason, now = clock.UtcNow }, ct);
        return (await ViewsAsync([row], ct)).Single();
    }

    // ---- Reading ----

    private async Task<Dictionary<Guid, AwardNominee>> NomineesAsync(string kind, IReadOnlyCollection<Guid> ids, CancellationToken ct)
    {
        if (ids.Count == 0)
        {
            return [];
        }
        if (kind == AwardKinds.Team)
        {
            var names = await hierarchy.GetNamesAsync(ids, ct);
            return ids.ToDictionary(i => i, i => new AwardNominee(i, null, names.TryGetValue(i, out var n) ? n : new Dictionary<string, string>()));
        }
        var people = await users.GetManyAsync(ids, ct);
        return ids.ToDictionary(i => i, i => new AwardNominee(i, people.TryGetValue(i, out var u) ? new Person(i, u.DisplayName, u.Status == "active") : new Person(i, "", false), null));
    }

    private async Task<List<AwardPeriodView>> ViewsAsync(IReadOnlyList<PeriodRow> rows, CancellationToken ct)
    {
        if (rows.Count == 0)
        {
            return [];
        }
        var me = context.RequireUser();
        var ids = rows.Select(r => r.Id).ToArray();
        var mine = (await session.QueryAsync("select period_id, nominee_id, reason from engagement.award_nomination where period_id = any(@ids) and nominator_id = @me",
            new { ids, me }, r => (Period: r.GetGuid(0), Nominee: r.GetGuid(1), Reason: r.GetString(2)), ct)).ToDictionary(n => n.Period);
        var winners = (await session.QueryAsync("select period_id, nominee_id from engagement.award_winner where period_id = any(@ids)", new { ids },
            r => (Period: r.GetGuid(0), Nominee: r.GetGuid(1)), ct)).ToLookup(w => w.Period, w => w.Nominee);
        var units = await hierarchy.GetNamesAsync(rows.Select(r => r.OrgUnitId).OfType<Guid>().Distinct(), ct);
        var manages = await ManagesAsync(ct);
        var today = await points.TodayAsync(ct);
        var result = new List<AwardPeriodView>();
        foreach (var row in rows)
        {
            var wanted = winners[row.Id].ToList();
            if (mine.TryGetValue(row.Id, out var n))
            {
                wanted.Add(n.Nominee);
            }
            var nominees = await NomineesAsync(row.Kind, wanted.Distinct().ToList(), ct);
            var decides = await DecidesAsync(row, ct);
            result.Add(new AwardPeriodView(row.Id, row.Kind, row.Title, row.OrgUnitId,
                row.OrgUnitId is { } u ? units.TryGetValue(u, out var names) ? names : new Dictionary<string, string>() : null,
                row.CloseOn, row.Status, row.Status == AwardStatuses.Open && today <= row.CloseOn,
                mine.TryGetValue(row.Id, out var m) ? new AwardMyNomination(nominees[m.Nominee], m.Reason) : null,
                winners[row.Id].Select(w => nominees[w]).ToList(),
                decides && row.Status == AwardStatuses.Open,
                decides || manages ? row.AnnouncementItemId : null,
                row.CreatedAt));
        }
        return result;
    }

    /// <summary>Recent periods, open ones first. Everyone who takes part sees them; nominations stay with their authors and the approvers.</summary>
    public async Task<AwardList> ListAsync(CancellationToken ct)
    {
        await RequireAsync(ct);
        var rows = await session.QueryAsync($"select {Columns} from engagement.award_period order by status = 'open' desc, created_at desc limit 50", null, Read, ct);
        return new AwardList(await ManagesAsync(ct), await ViewsAsync(rows, ct));
    }

    public async Task<AwardPeriodView> GetAsync(Guid id, CancellationToken ct)
    {
        await RequireAsync(ct);
        var row = await RowAsync(id, false, ct) ?? throw AppException.NotFound();
        return (await ViewsAsync([row], ct)).Single();
    }

    public async Task<IReadOnlyList<AwardNomineeSummary>> NominationsAsync(Guid id, CancellationToken ct)
    {
        await RequireAsync(ct);
        var row = await RowAsync(id, false, ct) ?? throw AppException.NotFound();
        if (!await DecidesAsync(row, ct))
        {
            throw AppException.Denied();
        }
        var rows = await session.QueryAsync("select nominator_id, nominee_id, reason, created_at from engagement.award_nomination where period_id = @id order by created_at",
            new { id }, r => (Nominator: r.GetGuid(0), Nominee: r.GetGuid(1), Reason: r.GetString(2), At: r.GetFieldValue<DateTimeOffset>(3)), ct);
        var nominees = await NomineesAsync(row.Kind, rows.Select(r => r.Nominee).Distinct().ToList(), ct);
        var people = await users.GetManyAsync(rows.Select(r => r.Nominator).Distinct().ToList(), ct);
        Person P(Guid u) => people.TryGetValue(u, out var p) ? new Person(u, p.DisplayName, p.Status == "active") : new Person(u, "", false);
        return rows.GroupBy(r => r.Nominee)
            .Select(g => new AwardNomineeSummary(nominees[g.Key], g.Count(), g.Select(r => new AwardReason(P(r.Nominator), r.Reason, r.At)).ToList()))
            .OrderByDescending(s => s.Count).ThenBy(s => s.Nominee.Person?.DisplayName ?? "", StringComparer.CurrentCulture).ToList();
    }

    // ---- Deciding ----

    public async Task<AwardPeriodView> DecideAsync(Guid id, AwardWinnersRequest request, CancellationToken ct)
    {
        await RequireAsync(ct);
        var row = await RowAsync(id, true, ct) ?? throw AppException.NotFound();
        if (!await DecidesAsync(row, ct))
        {
            throw AppException.Denied();
        }
        if (row.Status != AwardStatuses.Open)
        {
            throw AppException.Conflict("conflict.engagement.award_decided");
        }
        var picked = (request.NomineeIds ?? []).Distinct().ToList();
        var nominated = (await session.QueryAsync("select distinct nominee_id from engagement.award_nomination where period_id = @id", new { id }, r => r.GetGuid(0), ct)).ToHashSet();
        if (picked.Count is 0 or > MaxWinners || picked.Any(p => !nominated.Contains(p)))
        {
            throw AppException.Validation("nomineeIds", "validation.engagement.award_winners");
        }
        var me = context.RequireUser();
        var now = clock.UtcNow;
        foreach (var p in picked)
        {
            await session.ExecAsync("insert into engagement.award_winner (tenant_id, period_id, nominee_id) values (platform.current_tenant_id(), @id, @p)", new { id, p }, ct);
        }
        var nominees = await NomineesAsync(row.Kind, picked, ct);
        var item = await DraftAnnouncementAsync(row, picked.Select(p => nominees[p]).ToList(), ct);
        await session.ExecAsync(
            "update engagement.award_period set status = 'decided', decided_by = @me, decided_at = @now, announcement_item_id = @item where id = @id",
            new { id, me, now, item }, ct);
        await audit.WriteAsync(new AuditEntry(AuditActions.EngagementAwardDecided, "award_period", id.ToString(), TargetOrgUnitId: row.OrgUnitId,
            Changes: new { winners = picked.ToArray(), announcement = item }), ct);
        if (row.Kind == AwardKinds.Person)
        {
            await notifications.SendAsync(new NotificationRequest(picked, NotificationCategories.Recognition, WonTemplate, $"{AwardsPath}#award-{id}",
                new Dictionary<string, string> { ["award"] = row.Title }, DedupeKey: $"engagement.award:{id:N}"), ct);
        }
        return (await ViewsAsync([(await RowAsync(id, false, ct))!], ct)).Single();
    }

    /// <summary>
    /// The announcement draft in every enabled locale: a title naming the award and the winners as a list. The draft
    /// waits for a communicator; with no content in the package there is no draft, and the winners stand anyway.
    /// </summary>
    private async Task<Guid?> DraftAnnouncementAsync(PeriodRow row, IReadOnlyList<AwardNominee> winners, CancellationToken ct)
    {
        if (!await entitlements.IsEnabledAsync(Capabilities.ContentCore, ct))
        {
            return null;
        }
        var locales = (await tenantSettings.GetLocaleSettingsAsync(ct)).EnabledLocales;
        var titles = new Dictionary<string, string>(StringComparer.Ordinal);
        var bodies = new Dictionary<string, BodyDocument>(StringComparer.Ordinal);
        foreach (var locale in locales)
        {
            titles[locale] = localizer.Get("engagement.award.announcement.title", locale).Replace("{award}", row.Title, StringComparison.Ordinal);
            var names = winners.Select(w => w.Person?.DisplayName ?? Name(w.UnitNames, locale)).ToList();
            bodies[locale] = new BodyDocument(
            [
                new Block(BlockTypes.Paragraph, [new Inline(localizer.Get("engagement.award.announcement.intro", locale).Replace("{award}", row.Title, StringComparison.Ordinal))]),
                new Block(BlockTypes.List, Ordered: false, Items: names.Select(n => (IReadOnlyList<Inline>)[new Inline(n)]).ToList()),
            ]);
        }
        return await announcements.CreateAsync(new AnnouncementDraft(titles, bodies), ct);
    }

    private static string Name(IReadOnlyDictionary<string, string>? names, string locale) =>
        names is null ? "" : names.TryGetValue(locale, out var n) ? n : names.Values.FirstOrDefault() ?? "";
}
