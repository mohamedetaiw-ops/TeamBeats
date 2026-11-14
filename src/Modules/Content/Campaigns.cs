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
/// </summary>
public sealed record CampaignStepRequest(Guid ItemId, DateTimeOffset At, bool Mandatory = false, bool OnlyNotActed = false);

public sealed record CampaignRequest(string? Name, Guid? SiteId, ContentAudience? Audience, IReadOnlyList<CampaignStepRequest>? Steps, int? Version = null);

public sealed record CampaignStepView(Guid Id, int Position, Guid ItemId, IReadOnlyDictionary<string, string> ItemTitle, DateTimeOffset At, bool Mandatory,
    bool OnlyNotActed, string Status, DateTimeOffset? SentAt, int? Recipients);

public sealed record CampaignView(Guid Id, Guid SiteId, string Name, ContentAudience Audience, string Status, IReadOnlyList<CampaignStepView> Steps,
    ContentPerson CreatedBy, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, int Version);

/// <summary>Counts of a step or of the whole campaign; the counts of what people did are null below the minimum group size.</summary>
public sealed record CampaignCounts(int Audience, int Delivered, int? Viewed, int? Clicked, int? Acknowledged);

public sealed record CampaignStepStats(Guid StepId, int Position, string Status, CampaignCounts Counts);

/// <summary>A unit or location row of the breakdown; <see cref="Key"/> is null for "other", where small groups are merged.</summary>
public sealed record CampaignBreakdownRow(string? Key, IReadOnlyDictionary<string, string>? Names, int Audience, int Viewed, int Acknowledged);

public sealed record CampaignDashboard(Guid CampaignId, int MinimumGroupSize, CampaignCounts Total, IReadOnlyList<CampaignStepStats> Steps,
    IReadOnlyList<CampaignBreakdownRow> ByUnit, IReadOnlyList<CampaignBreakdownRow> ByLocation);

internal sealed record CampaignRow(Guid Id, Guid SiteId, string Name, ContentAudience Audience, string Status, Guid CreatedBy, DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt, int Version);

internal sealed record StepRow(Guid Id, Guid CampaignId, int Position, Guid ItemId, DateTimeOffset At, bool Mandatory, bool OnlyNotActed, string Status,
    DateTimeOffset? SentAt, int? Recipients, int? Delivered);

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

    private const string Columns = "id, site_id, name, audience::text, status, created_by, created_at, updated_at, row_version";

    private static CampaignRow Map(Npgsql.NpgsqlDataReader r) => new(r.GetGuid(0), r.GetGuid(1), r.GetString(2), ContentJson.Read<ContentAudience>(r.GetString(3)),
        r.GetString(4), r.GetGuid(5), r.GetFieldValue<DateTimeOffset>(6), r.GetFieldValue<DateTimeOffset>(7), r.GetInt32(8));

    private Task<List<StepRow>> StepsAsync(Guid campaign, CancellationToken ct) =>
        session.QueryAsync(
            """
            select id, campaign_id, position, item_id, at, mandatory, only_not_acted, status, sent_at, recipients, delivered
            from content.campaign_step where campaign_id = @campaign order by position
            """, new { campaign }, StepMap, ct);

    private static StepRow StepMap(Npgsql.NpgsqlDataReader r) => new(r.GetGuid(0), r.GetGuid(1), r.GetInt32(2), r.GetGuid(3), r.GetFieldValue<DateTimeOffset>(4),
        r.GetBoolean(5), r.GetBoolean(6), r.GetString(7), r.Get<DateTimeOffset?>("sent_at"), r.Get<int?>("recipients"), r.Get<int?>("delivered"));

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
        foreach (var s in await StepsAsync(row.Id, ct))
        {
            var item = await items.RowAsync(s.ItemId, false, ct);
            var number = item?.DraftVersion ?? item?.PublishedVersion;
            var version = number is { } n ? await items.VersionAsync(s.ItemId, n, ct) : null;
            var title = version?.Variants.ToDictionary(v => v.Key, v => v.Value.Title, StringComparer.Ordinal) ?? [];
            steps.Add(new CampaignStepView(s.Id, s.Position, s.ItemId, title, s.At, s.Mandatory, s.OnlyNotActed, s.Status, s.SentAt, s.Recipients));
        }
        return new CampaignView(row.Id, row.SiteId, row.Name, row.Audience, row.Status, steps, await items.PersonAsync(row.CreatedBy, ct), row.CreatedAt,
            row.UpdatedAt, row.Version);
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
        return audience;
    }

    private async Task WriteStepsAsync(Guid campaign, IReadOnlyList<CampaignStepRequest> steps, CancellationToken ct)
    {
        await session.ExecAsync("delete from content.campaign_step where campaign_id = @campaign", new { campaign }, ct);
        var position = 0;
        foreach (var step in steps.OrderBy(s => s.At))
        {
            await session.ExecAsync(
                """
                insert into content.campaign_step (tenant_id, id, campaign_id, position, item_id, at, mandatory, only_not_acted, status)
                values (platform.current_tenant_id(), @id, @campaign, @position, @item, @at, @mandatory, @notActed, 'pending')
                """, new { id = Ids.New(), campaign, position = ++position, item = step.ItemId, at = step.At, mandatory = step.Mandatory, notActed = step.OnlyNotActed }, ct);
        }
    }

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
        var audience = await ValidateAsync(request with { Name = request.Name ?? campaign.Name, Audience = request.Audience ?? campaign.Audience }, site, errors, ct);
        errors.ThrowIfAny();
        await session.ExecAsync(
            "update content.campaign set name = @name, audience = @audience, updated_at = @now, row_version = row_version + 1 where id = @id",
            new { id, name = (request.Name ?? campaign.Name).Trim(), audience = new Jsonb(ContentJson.Write(audience)), now = clock.UtcNow }, ct);
        if (request.Steps is { } steps)
        {
            await WriteStepsAsync(id, steps, ct);
        }
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
        var due = await session.QueryAsync(
            """
            select s.id from content.campaign_step s join content.campaign c on c.id = s.campaign_id
            where s.status = 'pending' and s.at <= @now and c.status = 'scheduled' order by s.at, s.id limit @limit
            """, new { now, limit }, r => r.GetGuid(0), ct);
        foreach (var stepId in due)
        {
            var step = (await session.QueryAsync(
                """
                select id, campaign_id, position, item_id, at, mandatory, only_not_acted, status, sent_at, recipients, delivered
                from content.campaign_step where id = @stepId for update
                """, new { stepId }, StepMap, ct)).FirstOrDefault();
            if (step is not { Status: CampaignStepStatuses.Pending } || await RowAsync(step.CampaignId, true, ct) is not { Status: CampaignStatuses.Scheduled } campaign)
            {
                continue;
            }
            await SendStepAsync(campaign, step, now, ct);
            if (!await session.ScalarAsync<bool>("select exists (select 1 from content.campaign_step where campaign_id = @id and status = 'pending')", new { id = campaign.Id }, ct))
            {
                await SetStatusAsync(campaign.Id, CampaignStatuses.Completed, ct);
            }
        }
        return due.Count;
    }

    private async Task SendStepAsync(CampaignRow campaign, StepRow step, DateTimeOffset now, CancellationToken ct)
    {
        var item = await items.RowAsync(step.ItemId, false, ct);
        var site = item is null ? null : await sites.RowAsync(item.SiteId, false, ct);
        if (item is not { Status: ContentStatuses.Published } || site is not { Active: true })
        {
            // Only live items are sent; a step whose item is not live is skipped and shows so on the dashboard.
            await session.ExecAsync("update content.campaign_step set status = 'skipped', sent_at = @now, recipients = 0, delivered = 0 where id = @id",
                new { id = step.Id, now }, ct);
            return;
        }
        var acted = step.OnlyNotActed ? await ActedAsync(campaign.Id, item, ct) : new HashSet<Guid>();
        var recipients = new List<Guid>();
        foreach (var user in (await audiences.MembersAsync(campaign.Audience, ct)).Order())
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
        return new CampaignDashboard(campaign.Id, MinimumGroupSize, total, bySteps, unitRows, Breakdown(byLocation));
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
