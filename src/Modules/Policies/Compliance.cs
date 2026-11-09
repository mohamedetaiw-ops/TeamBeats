using TeamBeats.Audit.Contracts;
using TeamBeats.Authorization.Contracts;
using TeamBeats.Entitlements.Contracts;
using TeamBeats.Messaging;
using TeamBeats.Modules.Identity.Contracts;
using TeamBeats.Modules.People.Contracts;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Primitives;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.Policies;

/// <summary>
/// The compliance view (spec 025 US3, FR-009): completion of a campaign for the version in force, by org unit with
/// subtree roll-up, by location and by line manager, with drill-down to people, and the per-person view. Each viewer
/// sees only their own people: compliance officers the people placed in the units where they hold
/// <c>policy.manage</c> (all of them at tenant scope), line managers their direct and indirect reports.
/// </summary>
public sealed class PolicyCompliance(
    IDbSession session,
    ITenantContext context,
    PolicyRegister register,
    PolicyCampaigns campaigns,
    IEntitlementService entitlements,
    IAuthorizationService authz,
    IOrganizationHierarchy hierarchy,
    IReportingLine reportingLine,
    IProfiles profiles,
    IUserDirectory users,
    IAuditWriter audit,
    IOutbox outbox,
    IClock clock)
{
    public const int PageMax = 200;
    public const int PageDefault = 50;

    private sealed record Viewer(OrgCoverage Coverage, IReadOnlySet<Guid> Reports)
    {
        public string Scope => Coverage.WholeTenant ? ComplianceScopes.Tenant : Coverage.Any ? ComplianceScopes.Units : ComplianceScopes.Reports;
    }

    private sealed record Member(Guid UserId, string Status, DateTimeOffset DueAt, DateTimeOffset? AttestedAt, DateTimeOffset? LeftAt);

    /// <summary>A member with where they sit: primary unit and its path from the root, location and manager.</summary>
    private sealed record Placed(Member Member, Guid? Unit, IReadOnlyList<Guid> Path, string? Location, Guid? Manager);

    private async Task<Viewer> ViewerAsync(CancellationToken ct)
    {
        await entitlements.DemandAsync(Capabilities.PolicyCore, ct);
        var me = context.RequireUser();
        var coverage = await authz.CoverageAsync(Permissions.PolicyManage, ct);
        var reports = coverage.WholeTenant ? new HashSet<Guid>() : await reportingLine.AllReportsAsync(me, ct);
        if (!coverage.Any && reports.Count == 0)
        {
            throw AppException.Denied();
        }
        return new Viewer(coverage, reports);
    }

    private sealed record Memberships(ILookup<Guid, UserOrgMembership> ByUser)
    {
        public Guid? Primary(Guid user)
        {
            var list = ByUser[user].ToList();
            return (list.FirstOrDefault(m => m.IsPrimary) ?? list.FirstOrDefault())?.OrgUnitId;
        }
    }

    private async Task<Memberships> MembershipsAsync(CancellationToken ct) =>
        new((await hierarchy.ListActiveMembershipsAsync(ct)).ToLookup(m => m.UserId));

    /// <summary>Whether the viewer may see this person: placed in a covered unit, or one of their reports.</summary>
    private static bool Sees(Viewer viewer, Memberships memberships, Guid user) =>
        viewer.Coverage.WholeTenant || viewer.Reports.Contains(user) || memberships.ByUser[user].Any(m => viewer.Coverage.Units.Contains(m.OrgUnitId));

    private async Task<IReadOnlyList<CampaignRow>> CampaignsOfAsync(Guid policyId, CancellationToken ct) =>
        await session.QueryAsync($"select {CampaignRow.Columns} from policies.campaign where policy_id = @policyId order by launched_at desc, id",
            new { policyId }, CampaignRow.Read, ct);

    /// <summary>The asked campaign, or the open one for the version in force, or the latest for that version.</summary>
    private static CampaignRow? Pick(PolicyRow policy, IReadOnlyList<CampaignRow> all, Guid? campaignId)
    {
        if (campaignId is { } id)
        {
            return all.FirstOrDefault(c => c.Id == id) ?? throw AppException.NotFound();
        }
        var current = all.Where(c => c.Version == policy.CurrentVersion).ToList();
        return current.FirstOrDefault(c => c.Status == "open") ?? current.FirstOrDefault();
    }

    private async Task<(PolicyRow Policy, Viewer Viewer, CampaignRow? Campaign, IReadOnlyList<CampaignRow> All, List<Placed> People)> LoadAsync(
        Guid id, Guid? campaignId, CancellationToken ct)
    {
        var viewer = await ViewerAsync(ct);
        var policy = await register.VisibleAsync(id, false, ct);
        var all = await CampaignsOfAsync(policy.Id, ct);
        var campaign = Pick(policy, all, campaignId);
        if (campaign is null)
        {
            return (policy, viewer, null, all, []);
        }
        var members = await session.QueryAsync(
            "select user_id, status, due_at, attested_at, left_at from policies.campaign_member where campaign_id = @c",
            new { c = campaign.Id }, r => new Member(r.GetGuid(0), r.GetString(1), r.GetFieldValue<DateTimeOffset>(2), r.Get<DateTimeOffset?>("attested_at"),
                r.Get<DateTimeOffset?>("left_at")), ct);
        var memberships = await MembershipsAsync(ct);
        var visible = members.Where(m => Sees(viewer, memberships, m.UserId)).ToList();
        var units = visible.Select(m => memberships.Primary(m.UserId)).OfType<Guid>().Distinct().ToList();
        var paths = await hierarchy.GetPathsAsync(units, ct);
        var placements = await profiles.PlacementsAsync(visible.Select(m => m.UserId).ToList(), ct);
        var people = visible.Select(m =>
        {
            var unit = memberships.Primary(m.UserId);
            var path = unit is { } u && paths.TryGetValue(u, out var p) ? p : [];
            placements.TryGetValue(m.UserId, out var placement);
            return new Placed(m, unit, path, placement?.LocationKey, placement?.ManagerId);
        }).ToList();
        return (policy, viewer, campaign, all, people);
    }

    private static ComplianceCounts Count(IEnumerable<Placed> people)
    {
        int members = 0, attested = 0, open = 0, overdue = 0, left = 0;
        foreach (var p in people)
        {
            members++;
            switch (p.Member.Status)
            {
                case MemberStatuses.Attested: attested++; break;
                case MemberStatuses.Open: open++; break;
                case MemberStatuses.Overdue: overdue++; break;
                default: left++; break;
            }
        }
        var inAudience = members - left;
        return new ComplianceCounts(members, attested, open, overdue, left, inAudience == 0 ? 0 : Math.Round(attested * 100.0 / inAudience, 1));
    }

    /// <summary>
    /// The unit the breakdown starts from: the asked one, or else the deepest unit every placed person sits under, so a
    /// tenant with one root organization opens on its subsidiaries.
    /// </summary>
    private static Guid? Anchor(IReadOnlyList<Placed> people, Guid? unit)
    {
        if (unit is not null)
        {
            return unit;
        }
        var paths = people.Select(p => p.Path).Where(p => p.Count > 0).ToList();
        if (paths.Count == 0)
        {
            return null;
        }
        Guid? anchor = null;
        for (var depth = 0; paths.All(p => p.Count > depth + 1) && paths.All(p => p[depth] == paths[0][depth]); depth++)
        {
            anchor = paths[0][depth];
        }
        return anchor;
    }

    private static bool Under(Placed p, Guid? anchor) => anchor is not { } a || p.Path.Contains(a);

    private static (Guid? Child, bool Direct) Branch(Placed p, Guid? anchor)
    {
        if (p.Path.Count == 0)
        {
            return (null, false);
        }
        if (anchor is not { } a)
        {
            return (p.Path[0], false);
        }
        var index = p.Path.ToList().IndexOf(a);
        return index == p.Path.Count - 1 ? (a, true) : (p.Path[index + 1], false);
    }

    private static string? NoneKey(string? location) => string.IsNullOrEmpty(location) ? null : location;

    /// <summary>Completion of a policy for the viewer's people (US3 scenarios 1, 3 and 4).</summary>
    public async Task<ComplianceView> ViewAsync(Guid id, Guid? campaignId, Guid? unit, CancellationToken ct)
    {
        var (policy, viewer, campaign, all, everyone) = await LoadAsync(id, campaignId, ct);
        var anchor = Anchor(everyone, unit);
        // People with no unit stay in the totals until a unit is chosen, as their own line.
        var people = unit is null ? everyone : everyone.Where(p => Under(p, anchor)).ToList();

        var byUnit = people.GroupBy(p => Branch(p, anchor)).ToList();
        var names = await hierarchy.GetNamesAsync(byUnit.Select(g => g.Key.Child).OfType<Guid>(), ct);
        var unitGroups = byUnit
            .Select(g => new ComplianceGroup(g.Key.Child, g.Key.Child is { } u && names.TryGetValue(u, out var n) ? n : null, g.Key.Direct, null, null, Count(g)))
            .OrderBy(g => g.UnitId is null).ThenBy(g => !g.Direct).ThenByDescending(g => g.Counts.Members).ToList();
        var locationGroups = people.GroupBy(p => NoneKey(p.Location))
            .Select(g => new ComplianceGroup(null, null, false, g.Key, null, Count(g)))
            .OrderBy(g => g.Location is null).ThenByDescending(g => g.Counts.Members).ThenBy(g => g.Location, StringComparer.Ordinal).ToList();
        var managerGroups = new List<ComplianceGroup>();
        foreach (var g in people.GroupBy(p => p.Manager))
        {
            managerGroups.Add(new ComplianceGroup(null, null, false, null, g.Key is { } m ? await register.PersonAsync(m, ct) : null, Count(g)));
        }
        managerGroups = managerGroups.OrderBy(g => g.Manager is null).ThenByDescending(g => g.Counts.Members).ThenBy(g => g.Manager?.DisplayName, StringComparer.CurrentCulture).ToList();

        var path = anchor is { } a ? (await hierarchy.GetPathsAsync([a], ct)).GetValueOrDefault(a) ?? [] : [];
        return new ComplianceView(policy.Id, policy.CurrentVersion, Ref(campaign), all.Select(c => Ref(c)!).ToList(), viewer.Scope, anchor, path, Count(people),
            unitGroups, locationGroups, managerGroups);
    }

    private static ComplianceCampaign? Ref(CampaignRow? c) => c is null ? null : new ComplianceCampaign(c.Id, c.Version, c.Deadline, c.Status);

    private static int StatusOrder(string status) => status switch
    {
        MemberStatuses.Overdue => 0,
        MemberStatuses.Open => 1,
        MemberStatuses.Attested => 2,
        _ => 3,
    };

    /// <summary>
    /// Drill-down to people: a unit's subtree, a location, a manager's direct reports or a status. <paramref name="location"/>
    /// and <paramref name="manager"/> take <c>none</c> for people with neither.
    /// </summary>
    public async Task<CompliancePeople> PeopleAsync(Guid id, Guid? campaignId, Guid? unit, bool direct, string? location, string? manager, string? status, int? offset,
        int? limit, CancellationToken ct)
    {
        var errors = new ValidationErrors();
        Guid? managerId = null;
        if (manager is not null and not "none")
        {
            if (Guid.TryParse(manager, out var m))
            {
                managerId = m;
            }
            else
            {
                errors.Add("manager", "validation.policies.compliance_filter");
            }
        }
        if (status is not null && status is not (MemberStatuses.Open or MemberStatuses.Attested or MemberStatuses.Overdue or MemberStatuses.LeftAudience))
        {
            errors.Add("status", "validation.policies.compliance_filter");
        }
        if (offset < 0 || limit is < 1 or > PageMax)
        {
            errors.Add("limit", "validation.policies.compliance_page");
        }
        errors.ThrowIfAny();

        var (_, _, _, _, everyone) = await LoadAsync(id, campaignId, ct);
        var filtered = everyone.Where(p =>
                (unit is not { } u || (direct ? p.Unit == u : p.Path.Contains(u)))
                && (location is null || (location == "none" ? NoneKey(p.Location) is null : p.Location == location))
                && (manager is null || (manager == "none" ? p.Manager is null : p.Manager == managerId))
                && (status is null || p.Member.Status == status))
            .OrderBy(p => StatusOrder(p.Member.Status)).ThenBy(p => p.Member.DueAt).ThenBy(p => p.Member.UserId).ToList();
        var skip = offset ?? 0;
        var take = limit ?? PageDefault;
        var page = new List<CompliancePerson>();
        foreach (var p in filtered.Skip(skip).Take(take))
        {
            page.Add(new CompliancePerson(await register.PersonAsync(p.Member.UserId, ct), p.Unit, NoneKey(p.Location), p.Manager, p.Member.Status, p.Member.DueAt,
                p.Member.AttestedAt, p.Member.LeftAt));
        }
        return new CompliancePeople(page, filtered.Count, skip, take);
    }

    /// <summary>The per-person view (US3 scenario 2), for a person the viewer may see.</summary>
    public async Task<PersonCompliance> PersonAsync(Guid userId, CancellationToken ct)
    {
        var viewer = await ViewerAsync(ct);
        var memberships = await MembershipsAsync(ct);
        if (!Sees(viewer, memberships, userId) || await users.GetAsync(userId, ct) is null)
        {
            throw AppException.NotFound();
        }
        return new PersonCompliance(await register.PersonAsync(userId, ct), await campaigns.PoliciesOfAsync(userId, ct));
    }

    /// <summary>
    /// Reminds the viewer's people who still owe an attestation in an open campaign (US3 scenario 4), all of them or the
    /// ones named. A line manager reaches only their reports.
    /// </summary>
    public async Task<ComplianceRemindResult> RemindAsync(Guid id, ComplianceRemindRequest request, CancellationToken ct)
    {
        var (policy, viewer, campaign, _, everyone) = await LoadAsync(id, request.CampaignId, ct);
        if (campaign is null)
        {
            throw AppException.NotFound();
        }
        if (campaign.Status != "open")
        {
            throw AppException.Conflict("conflict.policies.campaign_closed");
        }
        var named = request.UserIds is { Count: > 0 } ids ? ids.ToHashSet() : null;
        var pending = everyone
            .Where(p => p.Member.Status is MemberStatuses.Open or MemberStatuses.Overdue && (named is null || named.Contains(p.Member.UserId)))
            .Select(p => p.Member.UserId).ToArray();
        var me = context.RequireUser();
        var stamp = clock.UtcNow.ToString("yyyyMMddHHmm", System.Globalization.CultureInfo.InvariantCulture);
        foreach (var chunk in pending.Chunk(PolicyCampaigns.NoticeBatch))
        {
            await outbox.EnqueueAsync(new AttestationNoticeBatch(campaign.Id, PolicyCampaigns.ReminderTemplate, chunk, $"manual:{me:N}:{stamp}"), ct);
        }
        await audit.WriteAsync(new AuditEntry(AuditActions.PolicyCampaignReminded, Securables.Policy, policy.Id.ToString(), TargetOrgUnitId: policy.OrgUnitId,
            Changes: new { campaign = campaign.Id, recipients = pending.Length, scope = viewer.Scope }), ct);
        return new ComplianceRemindResult(campaign.Id, pending.Length);
    }
}
