using TeamBeats.Audit.Contracts;
using TeamBeats.Authorization.Contracts;
using TeamBeats.Entitlements.Contracts;
using TeamBeats.Messaging;
using TeamBeats.Modules.Content.Contracts;
using TeamBeats.Modules.Notifications.Contracts;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Primitives;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.Content;

public static class CampaignStatuses
{
    public const string Draft = "draft";
    public const string Scheduled = "scheduled";
    public const string Completed = "completed";
    public const string Cancelled = "cancelled";
}

public static class CampaignStepStatuses
{
    public const string Pending = "pending";
    public const string Sent = "sent";
    public const string Skipped = "skipped";
    public const string Cancelled = "cancelled";
}

/// <summary>
/// One step of a campaign: an item of the campaign's site sent at a time. A mandatory step goes as a critical
/// announcement (spec 015 mandatory category: preferences do not apply and it is labelled); an optional one as an
/// announcement, so each person's channel preferences apply. With <see cref="OnlyNotActed"/>, people who already acted
/// on the item (confirmed it when it asks for confirmation, otherwise opened it) are left out.
/// With <see cref="Follows"/> (R3, FR-026: the 1-based place of an earlier step in the request) and <see cref="DelayDays"/>,
/// the step has no fixed time: it is due that many days after the earlier step was sent and goes only to the people the
/// earlier step reached who have not acted on its item. <see cref="At"/> is then ignored.
/// </summary>
public sealed record CampaignStepRequest(Guid ItemId, DateTimeOffset At, bool Mandatory = false, bool OnlyNotActed = false, int? Follows = null, int? DelayDays = null);

/// <summary>A campaign goal (R3, FR-027): an item of the campaign's steps and the share of the audience, 1 to 100 percent, that acted on it.</summary>
public sealed record CampaignGoal(Guid ItemId, int Percent);

public sealed record CampaignRequest(string? Name, Guid? SiteId, ContentAudience? Audience, IReadOnlyList<CampaignStepRequest>? Steps, int? Version = null,
    CampaignGoal? Goal = null);

/// <summary>A step as planned; <see cref="Follows"/> is the position of the step it follows, and <see cref="At"/> the time it is planned for.</summary>
public sealed record CampaignStepView(Guid Id, int Position, Guid ItemId, IReadOnlyDictionary<string, string> ItemTitle, DateTimeOffset At, bool Mandatory,
    bool OnlyNotActed, string Status, DateTimeOffset? SentAt, int? Recipients, int? Follows = null, int? DelayDays = null);

public sealed record CampaignGoalView(Guid ItemId, int Percent, DateTimeOffset? ReachedAt);

public sealed record CampaignView(Guid Id, Guid SiteId, string Name, ContentAudience Audience, string Status, IReadOnlyList<CampaignStepView> Steps,
    ContentPerson CreatedBy, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, int Version, CampaignGoalView? Goal = null, bool Automation = false);

/// <summary>How far the goal is: the share of the audience that acted on its item, null below the minimum group size.</summary>
public sealed record CampaignGoalProgress(Guid ItemId, int Percent, int? ActedPercent, DateTimeOffset? ReachedAt);

/// <summary>Counts of a step or of the whole campaign; the counts of what people did are null below the minimum group size.</summary>
public sealed record CampaignCounts(int Audience, int Delivered, int? Viewed, int? Clicked, int? Acknowledged);

public sealed record CampaignStepStats(Guid StepId, int Position, string Status, CampaignCounts Counts);

/// <summary>A unit or location row of the breakdown; <see cref="Key"/> is null for "other", where small groups are merged.</summary>
public sealed record CampaignBreakdownRow(string? Key, IReadOnlyDictionary<string, string>? Names, int Audience, int Viewed, int Acknowledged);

public sealed record CampaignDashboard(Guid CampaignId, int MinimumGroupSize, CampaignCounts Total, IReadOnlyList<CampaignStepStats> Steps,
    IReadOnlyList<CampaignBreakdownRow> ByUnit, IReadOnlyList<CampaignBreakdownRow> ByLocation, CampaignGoalProgress? Goal = null);

internal sealed record CampaignRow(Guid Id, Guid SiteId, string Name, ContentAudience Audience, string Status, Guid CreatedBy, DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt, int Version, Guid? GoalItemId = null, int? GoalPercent = null, DateTimeOffset? GoalReachedAt = null);

internal sealed record StepRow(Guid Id, Guid CampaignId, int Position, Guid ItemId, DateTimeOffset At, bool Mandatory, bool OnlyNotActed, string Status,
    DateTimeOffset? SentAt, int? Recipients, int? Delivered, Guid? FollowsStepId = null, int? DelayDays = null);

/// <summary>
/// Campaigns across channels (spec 009 US8, FR-015). A campaign belongs to a site; those who publish at the site plan,
/// start and cancel it and see its dashboard. Steps are sent by the content scheduler through spec 015; the send log
/// keeps who each step went to, when they opened the item and whether they came through the campaign link.
/// </summary>
public sealed class ContentCampaigns(
    IDbSession session,
    ITenantContext context,
    IEntitlementService entitlements,
    IAuthorizationProvisioning provisioning,
    IOrganizationHierarchy hierarchy,
    Items items,
    Sites sites,
    Audiences audiences,
    AudienceFactsLoader facts,
    INotificationSender notifications,
    IOutbox outbox,
    IAuditWriter audit,
    IClock clock)
{
    public const int MaxSteps = 20;
    public const int MinimumGroupSize = 5;
    public const string Template = "content.campaign.step";
    public const string GoalReachedTemplate = "content.campaign.goal_reached";
    public const int MaxDelayDays = 60;
    private const int GoalChecks = 50;

    private const string Columns = "id, site_id, name, audience::text, status, created_by, created_at, updated_at, row_version, goal_item_id, goal_percent, goal_reached_at";

    private const string StepColumns = "id, campaign_id, position, item_id, at, mandatory, only_not_acted, status, sent_at, recipients, delivered, follows_step_id, delay_days";

    private static CampaignRow Map(Npgsql.NpgsqlDataReader r) => new(r.GetGuid(0), r.GetGuid(1), r.GetString(2), ContentJson.Read<ContentAudience>(r.GetString(3)),
        r.GetString(4), r.GetGuid(5), r.GetFieldValue<DateTimeOffset>(6), r.GetFieldValue<DateTimeOffset>(7), r.GetInt32(8), r.Get<Guid?>("goal_item_id"),
        r.Get<int?>("goal_percent"), r.Get<DateTimeOffset?>("goal_reached_at"));

    private Task<List<StepRow>> StepsAsync(Guid campaign, CancellationToken ct) =>
        session.QueryAsync($"select {StepColumns} from content.campaign_step where campaign_id = @campaign order by position", new { campaign }, StepMap, ct);

    private static StepRow StepMap(Npgsql.NpgsqlDataReader r) => new(r.GetGuid(0), r.GetGuid(1), r.GetInt32(2), r.GetGuid(3), r.GetFieldValue<DateTimeOffset>(4),
        r.GetBoolean(5), r.GetBoolean(6), r.GetString(7), r.Get<DateTimeOffset?>("sent_at"), r.Get<int?>("recipients"), r.Get<int?>("delivered"),
        r.Get<Guid?>("follows_step_id"), r.Get<int?>("delay_days"));

    private async Task<CampaignRow?> RowAsync(Guid id, bool forUpdate, CancellationToken ct) =>
        (await session.QueryAsync($"select {Columns} from content.campaign where id = @id{(forUpdate ? " for update" : "")}", new { id }, Map, ct)).FirstOrDefault();

    /// <summary>The campaign when the caller publishes at its site; anyone else does not find it.</summary>
    private async Task<(CampaignRow Campaign, SiteRow Site)> ManagedAsync(Guid id, bool forUpdate, CancellationToken ct)
    {
        await entitlements.DemandAsync(Capabilities.ContentCampaigns, ct);
        context.RequireUser();
        if (await RowAsync(id, forUpdate, ct) is not { } campaign || await sites.RowAsync(campaign.SiteId, false, ct) is not { } site
            || !await items.HoldsAsync(Permissions.ContentPublish, site, ct))
        {
            throw AppException.NotFound();
        }
        return (campaign, site);
    }

    public async Task<IReadOnlyList<CampaignView>> ListAsync(CancellationToken ct)
    {
        await entitlements.DemandAsync(Capabilities.ContentCampaigns, ct);
        context.RequireUser();
        var list = new List<CampaignView>();
        var allowed = new Dictionary<Guid, bool>();
        foreach (var row in await session.QueryAsync($"select {Columns} from content.campaign order by created_at desc, id limit 200", null, Map, ct))
        {
            if (!allowed.TryGetValue(row.SiteId, out var ok))
            {
                allowed[row.SiteId] = ok = await sites.RowAsync(row.SiteId, false, ct) is { } site && await items.HoldsAsync(Permissions.ContentPublish, site, ct);
            }
            if (ok)
            {
                list.Add(await ViewAsync(row, ct));
            }
        }
        return list;
    }

    public async Task<CampaignView> GetAsync(Guid id, CancellationToken ct) => await ViewAsync((await ManagedAsync(id, false, ct)).Campaign, ct);

    private async Task<CampaignView> ViewAsync(CampaignRow row, CancellationToken ct)
    {
        var steps = new List<CampaignStepView>();
        var rows = await StepsAsync(row.Id, ct);
        var positions = rows.ToDictionary(s => s.Id, s => s.Position);
        foreach (var s in rows)
        {
            var item = await items.RowAsync(s.ItemId, false, ct);
            var number = item?.DraftVersion ?? item?.PublishedVersion;
            var version = number is { } n ? await items.VersionAsync(s.ItemId, n, ct) : null;
            var title = version?.Variants.ToDictionary(v => v.Key, v => v.Value.Title, StringComparer.Ordinal) ?? [];
            steps.Add(new CampaignStepView(s.Id, s.Position, s.ItemId, title, s.At, s.Mandatory, s.OnlyNotActed, s.Status, s.SentAt, s.Recipients,
                s.FollowsStepId is { } f && positions.TryGetValue(f, out var p) ? p : null, s.DelayDays));
        }
        return new CampaignView(row.Id, row.SiteId, row.Name, row.Audience, row.Status, steps, await items.PersonAsync(row.CreatedBy, ct), row.CreatedAt,
            row.UpdatedAt, row.Version, row.GoalItemId is { } g ? new CampaignGoalView(g, row.GoalPercent ?? 100, row.GoalReachedAt) : null,
            await entitlements.IsEnabledAsync(Capabilities.ContentCampaignAutomation, ct));
    }

    private async Task<ContentAudience> ValidateAsync(CampaignRequest request, SiteRow site, ValidationErrors errors, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Name) || request.Name.Trim().Length > 200)
        {
            errors.Add("name", "validation.content.campaign_name");
        }
        var audience = request.Audience ?? site.DefaultAudience;
        AudienceRules.Validate(audience, "audience", errors);
        // Every unit and group must be this tenant's: another tenant's IDs are refused, not silently matched by nobody.
        var clauses = audience.AnyOf ?? [];
        var units = clauses.SelectMany(c => c?.OrgUnits ?? []).Where(u => u is not null).Select(u => u.Id).Distinct().ToList();
        var groups = clauses.SelectMany(c => c?.Groups ?? []).Distinct().ToList();
        foreach (var unit in units)
        {
            if (!await hierarchy.UnitExistsAsync(unit, ct))
            {
                errors.Add("audience", "validation.content.campaign_audience");
                break;
            }
        }
        if (groups.Count > 0 && (await provisioning.ExistingGroupsAsync(groups, ct)).Count != groups.Count)
        {
            errors.Add("audience", "validation.content.campaign_audience");
        }
        var steps = request.Steps ?? [];
        if (steps.Count > MaxSteps)
        {
            errors.Add("steps", "validation.content.campaign_steps");
        }
        foreach (var step in steps.Take(MaxSteps))
        {
            if (step is null || await items.RowAsync(step.ItemId, false, ct) is not { } item || item.SiteId != site.Id || item.Status == ContentStatuses.Archived)
            {
                errors.Add("steps", "validation.content.campaign_item");
                break;
            }
        }
        await ValidateAutomationAsync(steps, request.Goal, errors, ct);
        return audience;
    }

    /// <summary>
    /// Follow-ups and goals (R3, FR-026, FR-027) need the advanced automation feature; without it they are refused, never
    /// dropped. A step follows an earlier step of the request after 1 to 60 days; the goal is an item of the steps and 1 to 100 percent.
    /// </summary>
    private async Task ValidateAutomationAsync(IReadOnlyList<CampaignStepRequest> steps, CampaignGoal? goal, ValidationErrors errors, CancellationToken ct)
    {
        var follows = steps.Any(s => s is not null && (s.Follows is not null || s.DelayDays is not null));
        if ((follows || goal is not null) && !await entitlements.IsEnabledAsync(Capabilities.ContentCampaignAutomation, ct))
        {
            errors.Add(goal is not null ? "goal" : "steps", "validation.content.campaign_automation");
            return;
        }
        for (var i = 0; i < steps.Count && i < MaxSteps; i++)
        {
            var step = steps[i];
            if (step is null || (step.Follows is null && step.DelayDays is null))
            {
                continue;
            }
            if (step.Follows is not { } f || f < 1 || f > i || step.DelayDays is not { } d || d < 1 || d > MaxDelayDays)
            {
                errors.Add("steps", "validation.content.campaign_follows");
                break;
            }
        }
        if (goal is not null && (goal.Percent is < 1 or > 100 || !steps.Any(s => s is not null && s.ItemId == goal.ItemId)))
        {
            errors.Add("goal", "validation.content.campaign_goal");
        }
    }

    /// <summary>
    /// The plan of the steps: a fixed step at its time, a follow-up at the planned time of the step it follows plus its
    /// delay. Positions follow the planned times; a follow-up always comes after the step it follows.
    /// </summary>
    private static List<(Guid Id, CampaignStepRequest Step, DateTimeOffset At, int? Follows, int Index)> Plan(IReadOnlyList<CampaignStepRequest> steps)
    {
        var planned = new List<(Guid Id, CampaignStepRequest Step, DateTimeOffset At, int? Follows, int Index)>();
        for (var i = 0; i < steps.Count; i++)
        {
            var step = steps[i];
            var at = step.Follows is { } f && step.DelayDays is { } d ? planned[f - 1].At.AddDays(d) : step.At;
            planned.Add((Ids.New(), step, at, step.Follows is { } x ? x - 1 : null, i));
        }
        return planned.OrderBy(p => p.At).ThenBy(p => p.Index).ToList();
    }

    private async Task WriteStepsAsync(Guid campaign, IReadOnlyList<CampaignStepRequest> steps, CancellationToken ct)
    {
        await session.ExecAsync("delete from content.campaign_step where campaign_id = @campaign", new { campaign }, ct);
        var plan = Plan(steps);
        // Ids of the request's steps by their place in the request, so a follow-up points at the step it follows.
        var byIndex = plan.ToDictionary(p => p.Index, p => p.Id);
        var position = 0;
        foreach (var (id, step, at, follows, _) in plan)
        {
            await session.ExecAsync(
                """
                insert into content.campaign_step (tenant_id, id, campaign_id, position, item_id, at, mandatory, only_not_acted, status, follows_step_id, delay_days)
                values (platform.current_tenant_id(), @id, @campaign, @position, @item, @at, @mandatory, @notActed, 'pending', @follows, @delay)
                """, new
                {
                    id, campaign, position = ++position, item = step.ItemId, at, mandatory = step.Mandatory, notActed = step.OnlyNotActed,
                    follows = follows is { } f ? byIndex[f] : (Guid?)null, delay = follows is null ? null : step.DelayDays,
                }, ct);
        }
    }

    private Task WriteGoalAsync(Guid campaign, CampaignGoal? goal, CancellationToken ct) =>
        session.ExecAsync("update content.campaign set goal_item_id = @item, goal_percent = @percent where id = @campaign",
            new { campaign, item = goal?.ItemId, percent = goal?.Percent }, ct);

    public async Task<CampaignView> CreateAsync(CampaignRequest request, CancellationToken ct)
    {
        await entitlements.DemandAsync(Capabilities.ContentCampaigns, ct);
        var user = context.RequireUser();
        var site = request.SiteId is { } siteId ? await sites.RowAsync(siteId, false, ct) : await sites.DefaultAsync(ct);
        if (site is null)
        {
            throw AppException.NotFound();
        }
        if (!await items.HoldsAsync(Permissions.ContentPublish, site, ct))
        {
            throw AppException.Denied();
        }
        if (!site.Active)
        {
            throw AppException.Conflict("conflict.content.site_archived");
        }
        var errors = new ValidationErrors();
        var audience = await ValidateAsync(request, site, errors, ct);
        errors.ThrowIfAny();
        var id = Ids.New();
        var now = clock.UtcNow;
        await session.ExecAsync(
            """
            insert into content.campaign (tenant_id, id, site_id, name, audience, status, created_by, created_at, updated_at)
            values (platform.current_tenant_id(), @id, @site, @name, @audience, 'draft', @user, @now, @now)
            """, new { id, site = site.Id, name = request.Name!.Trim(), audience = new Jsonb(ContentJson.Write(audience)), user, now }, ct);
        await WriteStepsAsync(id, request.Steps ?? [], ct);
        await WriteGoalAsync(id, request.Goal, ct);
        await audit.WriteAsync(new AuditEntry(AuditActions.ContentCampaignCreated, "content_campaign", id.ToString(), TargetOrgUnitId: site.OrgUnitId), ct);
        return await ViewAsync((await RowAsync(id, false, ct))!, ct);
    }

    /// <summary>A campaign is changed only while it is a draft; once started, its plan stays as people received it.</summary>
    public async Task<CampaignView> UpdateAsync(Guid id, CampaignRequest request, CancellationToken ct)
    {
        var (campaign, site) = await ManagedAsync(id, forUpdate: true, ct);
        if (request.Version is { } v && v != campaign.Version)
        {
            throw AppException.Conflict("conflict.content.changed");
        }
        if (campaign.Status != CampaignStatuses.Draft)
        {
            throw AppException.Conflict("conflict.content.campaign_started");
        }
        var errors = new ValidationErrors();
        // A goal is checked against the steps that will be in force; new steps without a goal clear it, no steps keep it.
        var goal = request.Goal ?? (request.Steps is null && campaign.GoalItemId is { } g ? new CampaignGoal(g, campaign.GoalPercent ?? 100) : null);
        var steps = request.Steps ?? (request.Goal is null ? null : (await StepsAsync(id, ct)).Select(s => new CampaignStepRequest(s.ItemId, s.At)).ToList());
        var audience = await ValidateAsync(request with { Name = request.Name ?? campaign.Name, Audience = request.Audience ?? campaign.Audience, Steps = steps }, site,
            errors, ct);
        errors.ThrowIfAny();
        await session.ExecAsync(
            "update content.campaign set name = @name, audience = @audience, updated_at = @now, row_version = row_version + 1 where id = @id",
            new { id, name = (request.Name ?? campaign.Name).Trim(), audience = new Jsonb(ContentJson.Write(audience)), now = clock.UtcNow }, ct);
        if (request.Steps is { } newSteps)
        {
            await WriteStepsAsync(id, newSteps, ct);
        }
        await WriteGoalAsync(id, goal, ct);
        return await ViewAsync((await RowAsync(id, false, ct))!, ct);
    }

    public async Task<CampaignView> StartAsync(Guid id, CancellationToken ct)
    {
        var (campaign, site) = await ManagedAsync(id, forUpdate: true, ct);
        if (campaign.Status != CampaignStatuses.Draft)
        {
            throw AppException.Conflict("conflict.content.campaign_started");
        }
        if (!site.Active)
        {
            throw AppException.Conflict("conflict.content.site_archived");
        }
        if ((await StepsAsync(id, ct)).Count == 0)
        {
            throw AppException.Conflict("conflict.content.campaign_empty");
        }
        await SetStatusAsync(id, CampaignStatuses.Scheduled, ct);
        await audit.WriteAsync(new AuditEntry(AuditActions.ContentCampaignStarted, "content_campaign", id.ToString(), TargetOrgUnitId: site.OrgUnitId), ct);
        return await ViewAsync((await RowAsync(id, false, ct))!, ct);
    }

    public async Task<CampaignView> CancelAsync(Guid id, CancellationToken ct)
    {
        var (campaign, site) = await ManagedAsync(id, forUpdate: true, ct);
        if (campaign.Status is CampaignStatuses.Completed or CampaignStatuses.Cancelled)
        {
            throw AppException.Conflict("conflict.content.campaign_ended");
        }
        await session.ExecAsync("update content.campaign_step set status = 'cancelled' where campaign_id = @id and status = 'pending'", new { id }, ct);
        await SetStatusAsync(id, CampaignStatuses.Cancelled, ct);
        await audit.WriteAsync(new AuditEntry(AuditActions.ContentCampaignCancelled, "content_campaign", id.ToString(), TargetOrgUnitId: site.OrgUnitId), ct);
        return await ViewAsync((await RowAsync(id, false, ct))!, ct);
    }

    private Task SetStatusAsync(Guid id, string status, CancellationToken ct) =>
        session.ExecAsync("update content.campaign set status = @status, updated_at = @now, row_version = row_version + 1 where id = @id",
            new { id, status, now = clock.UtcNow }, ct);

    // ---- Sending ----

    /// <summary>Sends the due steps of started campaigns, as the system actor. Returns how many steps were handled.</summary>
    public async Task<int> RunDueAsync(DateTimeOffset now, int limit, CancellationToken ct)
    {
        // Goals first, so a campaign that reached its goal sends no more steps (FR-027).
        var goals = await session.QueryAsync(
            "select id from content.campaign where status = 'scheduled' and goal_item_id is not null order by updated_at, id limit @limit",
            new { limit = GoalChecks }, r => r.GetGuid(0), ct);
        foreach (var campaignId in goals)
        {
            if (await RowAsync(campaignId, true, ct) is { Status: CampaignStatuses.Scheduled, GoalItemId: not null } campaign)
            {
                await CheckGoalAsync(campaign, now, ct);
            }
        }
        var due = await session.QueryAsync(
            """
            select s.id from content.campaign_step s join content.campaign c on c.id = s.campaign_id
            where s.status = 'pending' and s.at <= @now and c.status = 'scheduled' order by s.at, s.id limit @limit
            """, new { now, limit }, r => r.GetGuid(0), ct);
        foreach (var stepId in due)
        {
            var step = (await session.QueryAsync($"select {StepColumns} from content.campaign_step where id = @stepId for update", new { stepId }, StepMap, ct))
                .FirstOrDefault();
            if (step is not { Status: CampaignStepStatuses.Pending } || await RowAsync(step.CampaignId, true, ct) is not { Status: CampaignStatuses.Scheduled } campaign)
            {
                continue;
            }
            StepRow? followed = null;
            if (step.FollowsStepId is { } followsId)
            {
                followed = (await session.QueryAsync($"select {StepColumns} from content.campaign_step where id = @followsId", new { followsId }, StepMap, ct)).FirstOrDefault();
                if (followed is { Status: CampaignStepStatuses.Pending })
                {
                    continue; // The earlier step has not gone yet; its sending sets this step's time.
                }
                if (followed is { Status: CampaignStepStatuses.Sent, SentAt: { } sentAt } && sentAt.AddDays(step.DelayDays ?? 0) > now)
                {
                    await session.ExecAsync("update content.campaign_step set at = @at where id = @id", new { id = step.Id, at = sentAt.AddDays(step.DelayDays ?? 0) }, ct);
                    continue;
                }
                if (followed is not { Status: CampaignStepStatuses.Sent })
                {
                    // A follow-up of a step that was skipped or cancelled is skipped too.
                    await SkipAsync(step.Id, now, ct);
                    await CompleteWhenDoneAsync(campaign.Id, ct);
                    continue;
                }
            }
            await SendStepAsync(campaign, step, followed, now, ct);
            await CompleteWhenDoneAsync(campaign.Id, ct);
        }
        return due.Count;
    }

    private async Task CompleteWhenDoneAsync(Guid campaign, CancellationToken ct)
    {
        if (!await session.ScalarAsync<bool>("select exists (select 1 from content.campaign_step where campaign_id = @campaign and status = 'pending')", new { campaign }, ct))
        {
            await SetStatusAsync(campaign, CampaignStatuses.Completed, ct);
        }
    }

    private Task SkipAsync(Guid step, DateTimeOffset now, CancellationToken ct) =>
        session.ExecAsync("update content.campaign_step set status = 'skipped', sent_at = @now, recipients = 0, delivered = 0 where id = @step", new { step, now }, ct);

    /// <summary>
    /// The goal check (FR-027): the share of the campaign's current audience that acted on the goal item. When reached, the
    /// pending steps are skipped, the campaign is completed, the people who publish at the site are told and it is audited.
    /// </summary>
    private async Task CheckGoalAsync(CampaignRow campaign, DateTimeOffset now, CancellationToken ct)
    {
        if (await GoalShareAsync(campaign, ct) is not { } share || share.Percent < campaign.GoalPercent)
        {
            return;
        }
        await session.ExecAsync("update content.campaign_step set status = 'skipped', sent_at = @now, recipients = 0, delivered = 0 where campaign_id = @id and status = 'pending'",
            new { id = campaign.Id, now }, ct);
        await session.ExecAsync(
            "update content.campaign set status = 'completed', goal_reached_at = @now, updated_at = @now, row_version = row_version + 1 where id = @id",
            new { id = campaign.Id, now }, ct);
        var site = await sites.RowAsync(campaign.SiteId, false, ct);
        if (site is not null)
        {
            var candidates = new HashSet<Guid>(await provisioning.TenantHoldersAsync(Permissions.ContentPublish, ct)) { campaign.CreatedBy };
            foreach (var holder in await provisioning.OrgHoldersAsync(Permissions.ContentPublish, ct))
            {
                candidates.Add(holder.UserId);
            }
            var publishers = new List<Guid>();
            foreach (var user in candidates.Order())
            {
                if (await provisioning.UserHoldsAsync(user, Permissions.ContentPublish, site.Target, ct))
                {
                    publishers.Add(user);
                }
            }
            if (publishers.Count > 0)
            {
                await notifications.SendAsync(new NotificationRequest(publishers, NotificationCategories.DocumentUpdate, GoalReachedTemplate, CampaignPage(campaign.Id),
                    DedupeKey: $"content:campaign:{campaign.Id:N}:goal"), ct);
            }
        }
        await audit.WriteAsync(new AuditEntry(AuditActions.CampaignGoalReached, "content_campaign", campaign.Id.ToString(), TargetOrgUnitId: site?.OrgUnitId,
            Changes: new { item = campaign.GoalItemId, percent = campaign.GoalPercent, acted = share.Acted, audience = share.Audience }), ct);
    }

    /// <summary>Who of the current audience acted on the goal item, as counts and a whole percent; null without a goal, its item or an audience.</summary>
    private async Task<(int Acted, int Audience, int Percent)?> GoalShareAsync(CampaignRow campaign, CancellationToken ct)
    {
        if (campaign.GoalItemId is not { } goalItem || await items.RowAsync(goalItem, false, ct) is not { } item)
        {
            return null;
        }
        var members = (await audiences.MembersAsync(campaign.Audience, ct)).ToHashSet();
        if (members.Count == 0)
        {
            return null;
        }
        var acted = (await ActedAsync(campaign.Id, item, ct)).Count(members.Contains);
        return (acted, members.Count, acted * 100 / members.Count);
    }

    public static string CampaignPage(Guid campaign) => $"/content/campaigns/{campaign}";

    private async Task SendStepAsync(CampaignRow campaign, StepRow step, StepRow? followed, DateTimeOffset now, CancellationToken ct)
    {
        var item = await items.RowAsync(step.ItemId, false, ct);
        var site = item is null ? null : await sites.RowAsync(item.SiteId, false, ct);
        if (item is not { Status: ContentStatuses.Published } || site is not { Active: true })
        {
            // Only live items are sent; a step whose item is not live is skipped and shows so on the dashboard.
            await SkipAsync(step.Id, now, ct);
            return;
        }
        var acted = step.OnlyNotActed ? await ActedAsync(campaign.Id, item, ct) : new HashSet<Guid>();
        IEnumerable<Guid> audience;
        if (followed is not null)
        {
            // A follow-up goes to the people the earlier step reached who have not acted on its item (FR-026).
            var reached = await session.QueryAsync("select user_id from content.campaign_send where step_id = @step", new { step = followed.Id }, r => r.GetGuid(0), ct);
            var earlier = await items.RowAsync(followed.ItemId, false, ct);
            var done = earlier is null ? new HashSet<Guid>() : await ActedAsync(campaign.Id, earlier, ct);
            audience = reached.Where(u => !done.Contains(u));
        }
        else
        {
            audience = await audiences.MembersAsync(campaign.Audience, ct);
        }
        var recipients = new List<Guid>();
        foreach (var user in audience.Order())
        {
            // Targeting is not access: a step reaches only people who may read the item.
            if (!acted.Contains(user) && await items.UserMayReadAsync(user, item, site, ct))
            {
                recipients.Add(user);
            }
        }
        var category = step.Mandatory ? NotificationCategories.CriticalAnnouncement : NotificationCategories.Announcement;
        var delivered = 0;
        foreach (var chunk in recipients.Chunk(500))
        {
            await session.ExecAsync(
                """
                insert into content.campaign_send (tenant_id, step_id, campaign_id, item_id, user_id, sent_at)
                select platform.current_tenant_id(), @step, @campaign, @item, u, @now from unnest(@users) as u on conflict do nothing
                """, new { step = step.Id, campaign = campaign.Id, item = item.Id, users = chunk, now }, ct);
            var result = await notifications.SendAsync(new NotificationRequest(chunk, category, Template, CampaignLink(item.Id, step.Id),
                DedupeKey: $"content:campaign:{step.Id:N}"), ct);
            delivered += result.Created + result.Grouped;
        }
        await session.ExecAsync("update content.campaign_step set status = 'sent', sent_at = @now, recipients = @count, delivered = @delivered where id = @id",
            new { id = step.Id, now, count = recipients.Count, delivered }, ct);
        // Follow-ups of this step are now due their delay after this send.
        await session.ExecAsync("update content.campaign_step set at = @now + make_interval(days => delay_days) where follows_step_id = @id and status = 'pending'",
            new { id = step.Id, now }, ct);
        await outbox.EnqueueAsync(new CampaignStepSent(campaign.Id, step.Id, item.Id, recipients.Count), ct);
        await audit.WriteAsync(new AuditEntry(AuditActions.ContentCampaignStepSent, "content_campaign", campaign.Id.ToString(), TargetOrgUnitId: site.OrgUnitId,
            Changes: new { step = step.Position, recipients = recipients.Count, mandatory = step.Mandatory }), ct);
    }

    public static string CampaignLink(Guid item, Guid step) => $"/content/read/{item}?c={step:N}";

    /// <summary>
    /// Who already did what the item asks: confirmed it when it asks for confirmation, otherwise opened it after a step of
    /// this campaign reached them.
    /// </summary>
    private async Task<HashSet<Guid>> ActedAsync(Guid campaign, ItemRow item, CancellationToken ct) =>
        (item.Critical is { Confirms: true }
            ? await session.QueryAsync("select distinct user_id from content.acknowledgement where item_id = @item", new { item = item.Id }, r => r.GetGuid(0), ct)
            : await session.QueryAsync("select distinct user_id from content.campaign_send where campaign_id = @campaign and item_id = @item and viewed_at is not null",
                new { campaign, item = item.Id }, r => r.GetGuid(0), ct)).ToHashSet();

    /// <summary>
    /// A reader opened the item: each campaign send of it to them counts as viewed, and as clicked when they came through
    /// that step's link. Only people a campaign reached are recorded.
    /// </summary>
    public async Task SeenAsync(Guid item, string? step, CancellationToken ct)
    {
        if (context.ActorType != ActorType.User || context.ActorId is not { } user)
        {
            return;
        }
        Guid? clicked = step is not null && Guid.TryParseExact(step, "N", out var s) ? s : null;
        await session.ExecAsync(
            """
            update content.campaign_send set viewed_at = coalesce(viewed_at, @now),
              clicked_at = case when step_id = @clicked then coalesce(clicked_at, @now) else clicked_at end
            where user_id = @user and item_id = @item and (viewed_at is null or (step_id = @clicked and clicked_at is null))
            """, new { item, user, clicked, now = clock.UtcNow }, ct);
    }

    // ---- Dashboard ----

    public async Task<CampaignDashboard> DashboardAsync(Guid id, CancellationToken ct)
    {
        var (campaign, _) = await ManagedAsync(id, false, ct);
        var steps = await StepsAsync(id, ct);
        var sends = await session.QueryAsync(
            """
            select s.step_id, s.user_id, s.viewed_at is not null, s.clicked_at is not null,
              exists (select 1 from content.acknowledgement a where a.item_id = s.item_id and a.user_id = s.user_id)
            from content.campaign_send s where s.campaign_id = @id
            """, new { id }, r => (Step: r.GetGuid(0), User: r.GetGuid(1), Viewed: r.GetBoolean(2), Clicked: r.GetBoolean(3), Acked: r.GetBoolean(4)), ct);
        var bySteps = steps.Select(s =>
        {
            var mine = sends.Where(x => x.Step == s.Id).ToList();
            return new CampaignStepStats(s.Id, s.Position, s.Status, Counts(mine.Count, s.Delivered ?? 0, mine.Count(x => x.Viewed), mine.Count(x => x.Clicked), mine.Count(x => x.Acked)));
        }).ToList();
        var people = sends.GroupBy(x => x.User).Select(g => (User: g.Key, Viewed: g.Any(x => x.Viewed), Clicked: g.Any(x => x.Clicked), Acked: g.Any(x => x.Acked))).ToList();
        var total = Counts(people.Count, steps.Sum(s => s.Delivered ?? 0), people.Count(p => p.Viewed), people.Count(p => p.Clicked), people.Count(p => p.Acked));

        var byUnit = new Dictionary<string, List<(bool Viewed, bool Acked)>>(StringComparer.Ordinal);
        var byLocation = new Dictionary<string, List<(bool Viewed, bool Acked)>>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in people)
        {
            var f = await facts.ForAsync(p.User, ct);
            foreach (var unit in f.DirectUnits)
            {
                Add(byUnit, unit.ToString(), (p.Viewed, p.Acked));
            }
            if (!string.IsNullOrWhiteSpace(f.Location))
            {
                Add(byLocation, f.Location, (p.Viewed, p.Acked));
            }
        }
        var unitRows = Breakdown(byUnit);
        var names = await hierarchy.GetNamesAsync(unitRows.Where(r => r.Key is not null).Select(r => Guid.Parse(r.Key!)), ct);
        unitRows = unitRows.Select(r => r.Key is { } k && names.TryGetValue(Guid.Parse(k), out var n) ? r with { Names = n } : r).ToList();
        CampaignGoalProgress? goal = null;
        if (campaign.GoalItemId is { } goalItem)
        {
            var share = await GoalShareAsync(campaign, ct);
            goal = new CampaignGoalProgress(goalItem, campaign.GoalPercent ?? 100, share is { Audience: >= MinimumGroupSize } ? share.Value.Percent : null, campaign.GoalReachedAt);
        }
        return new CampaignDashboard(campaign.Id, MinimumGroupSize, total, bySteps, unitRows, Breakdown(byLocation), goal);
    }

    /// <summary>The raw funnel of a campaign for the analytics workspace (spec 021 US2); the same rights as the dashboard.</summary>
    public async Task<TeamBeats.Modules.Analytics.Contracts.AnalyticsCampaignFunnel> FunnelAsync(Guid id, CancellationToken ct)
    {
        var (campaign, _) = await ManagedAsync(id, false, ct);
        var view = await ViewAsync(campaign, ct);
        var steps = await StepsAsync(id, ct);
        var confirms = new Dictionary<Guid, bool>();
        foreach (var itemId in steps.Select(s => s.ItemId).Distinct())
        {
            confirms[itemId] = await items.RowAsync(itemId, false, ct) is { Critical.Confirms: true };
        }
        var sends = await session.QueryAsync(
            """
            select s.step_id, s.item_id, s.user_id, s.viewed_at is not null, s.clicked_at is not null,
              exists (select 1 from content.acknowledgement a where a.item_id = s.item_id and a.user_id = s.user_id)
            from content.campaign_send s where s.campaign_id = @id
            """, new { id }, r => (Step: r.GetGuid(0), Item: r.GetGuid(1), User: r.GetGuid(2), Viewed: r.GetBoolean(3), Clicked: r.GetBoolean(4), Acked: r.GetBoolean(5)), ct);
        bool Completed((Guid Step, Guid Item, Guid User, bool Viewed, bool Clicked, bool Acked) x) => confirms.GetValueOrDefault(x.Item) ? x.Acked : x.Viewed;
        var stepViews = view.Steps.ToDictionary(s => s.Id);
        var funnelSteps = steps.Select(s =>
        {
            var mine = sends.Where(x => x.Step == s.Id).ToList();
            var titles = stepViews.TryGetValue(s.Id, out var sv) ? sv.ItemTitle : new Dictionary<string, string>();
            return new TeamBeats.Modules.Analytics.Contracts.AnalyticsCampaignStep(s.Id, s.Position, s.ItemId, titles, s.Status, s.At, s.SentAt, CampaignLink(s.ItemId, s.Id),
                confirms.GetValueOrDefault(s.ItemId), mine.Count, s.Delivered ?? 0, mine.Count(x => x.Viewed), mine.Count(x => x.Clicked), mine.Count(Completed));
        }).ToList();
        var people = sends.GroupBy(x => x.User)
            .Select(g => (User: g.Key, Viewed: g.Any(x => x.Viewed), Clicked: g.Any(x => x.Clicked), Completed: g.Any(Completed))).ToList();
        var groups = new Dictionary<(string, string), (int Audience, int Viewed, int Completed)>();
        void Count(string dimension, string key, bool viewed, bool completed)
        {
            var c = groups.GetValueOrDefault((dimension, key));
            groups[(dimension, key)] = (c.Audience + 1, c.Viewed + (viewed ? 1 : 0), c.Completed + (completed ? 1 : 0));
        }
        foreach (var p in people)
        {
            var f = await facts.ForAsync(p.User, ct);
            foreach (var unit in f.DirectUnits)
            {
                Count("unit", unit.ToString(), p.Viewed, p.Completed);
            }
            if (!string.IsNullOrWhiteSpace(f.Location))
            {
                Count("location", f.Location, p.Viewed, p.Completed);
            }
        }
        var summary = new TeamBeats.Modules.Analytics.Contracts.AnalyticsCampaign(campaign.Id, campaign.Name, campaign.SiteId, campaign.Status, campaign.CreatedAt,
            steps.Count, steps.Where(s => s.SentAt is not null).Select(s => s.SentAt).Min());
        return new TeamBeats.Modules.Analytics.Contracts.AnalyticsCampaignFunnel(summary, funnelSteps, people.Count, people.Count(p => p.Viewed), people.Count(p => p.Clicked),
            people.Count(p => p.Completed), groups.Select(g => new TeamBeats.Modules.Analytics.Contracts.AnalyticsCampaignGroup(g.Key.Item1, g.Key.Item2, g.Value.Audience,
                g.Value.Viewed, g.Value.Completed)).ToList());
    }

    /// <summary>The campaigns the person may run, as a short list for the analytics workspace (spec 021 US2).</summary>
    public async Task<IReadOnlyList<TeamBeats.Modules.Analytics.Contracts.AnalyticsCampaign>> AnalyticsListAsync(CancellationToken ct)
    {
        var list = new List<TeamBeats.Modules.Analytics.Contracts.AnalyticsCampaign>();
        foreach (var c in await ListAsync(ct))
        {
            list.Add(new(c.Id, c.Name, c.SiteId, c.Status, c.CreatedAt, c.Steps.Count, c.Steps.Where(s => s.SentAt is not null).Select(s => s.SentAt).Min()));
        }
        return list;
    }

    private static void Add(Dictionary<string, List<(bool, bool)>> groups, string key, (bool, bool) value)
    {
        if (!groups.TryGetValue(key, out var list))
        {
            groups[key] = list = [];
        }
        list.Add(value);
    }

    /// <summary>What people did is shown only for groups of at least <see cref="MinimumGroupSize"/> (spec 021 US1 scenario 2).</summary>
    private static CampaignCounts Counts(int audience, int delivered, int viewed, int clicked, int acked) =>
        audience >= MinimumGroupSize ? new CampaignCounts(audience, delivered, viewed, clicked, acked) : new CampaignCounts(audience, delivered, null, null, null);

    /// <summary>Groups below the minimum size are merged into "other", which is left out when it is still too small.</summary>
    private static List<CampaignBreakdownRow> Breakdown(Dictionary<string, List<(bool Viewed, bool Acked)>> groups)
    {
        var rows = groups.Where(g => g.Value.Count >= MinimumGroupSize).OrderByDescending(g => g.Value.Count).ThenBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => new CampaignBreakdownRow(g.Key, null, g.Value.Count, g.Value.Count(x => x.Viewed), g.Value.Count(x => x.Acked))).ToList();
        var rest = groups.Where(g => g.Value.Count < MinimumGroupSize).SelectMany(g => g.Value).ToList();
        if (rest.Count >= MinimumGroupSize)
        {
            rows.Add(new CampaignBreakdownRow(null, null, rest.Count, rest.Count(x => x.Viewed), rest.Count(x => x.Acked)));
        }
        return rows;
    }
}
