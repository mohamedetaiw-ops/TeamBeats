using TeamBeats.Audit.Contracts;
using TeamBeats.Authorization.Contracts;
using TeamBeats.Entitlements.Contracts;
using TeamBeats.Messaging;
using TeamBeats.Modules.Integrations.Contracts;
using TeamBeats.Modules.Notifications.Contracts;
using TeamBeats.Modules.Policies.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Primitives;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.Policies;

public static class RegulatorChangeStatuses
{
    public const string New = "new";
    public const string Dismissed = "dismissed";
    public const string Actioned = "actioned";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal) { New, Dismissed, Actioned };
}

public sealed record RegulatorDecisionRequest(string? Note);

public sealed record RegulatorLinkRequest(IReadOnlyList<Guid>? PolicyIds, bool? StartReviews, string? Note);

/// <summary>A linked policy as the reader may see it, and whether a review was started for the change.</summary>
public sealed record RegulatorLinkedPolicy(PolicySummary Policy, bool ReviewStarted);

public sealed record RegulatorChangeView(
    Guid Id,
    string Source,
    string Title,
    string? Link,
    DateTimeOffset? PublishedAt,
    string? Summary,
    DateTimeOffset ReceivedAt,
    string Status,
    string? Note,
    PolicyPerson? DecidedBy,
    DateTimeOffset? DecidedAt,
    IReadOnlyList<RegulatorLinkedPolicy> Policies);

public sealed record RegulatorChangesView(bool Available, IReadOnlyList<RegulatorFeedConnection> Feeds, IReadOnlyList<RegulatorChangeView> Items);

/// <summary>What one maintenance pass did: feeds read, new items kept, items deleted after 2 years.</summary>
public sealed record RegulatorRun(int Read, int Added, int Purged);

/// <summary>
/// Regulator change feeds (spec 025 R3 US10, FR-024, FR-025). The maintenance job reads each enabled feed connection
/// through <see cref="IRegulatorFeeds"/> at most every 6 hours, keeps new items (id, title, https link, date, plain
/// summary) and tells the tenant's compliance officers once per read that found new items. People who manage policies
/// list the items, mark one as not relevant, or link it to policies they manage and start their reviews: each linked
/// policy in force without an open review gets a review due in 30 days, told to its owner like a scheduled review and
/// naming the change. Items are deleted 2 years after they were read.
/// </summary>
public sealed class PolicyRegulatorChanges(
    IDbSession session,
    ITenantContext context,
    PolicyRegister register,
    IRegulatorFeeds feeds,
    IEntitlementService entitlements,
    IAuthorizationService authz,
    IAuthorizationProvisioning holders,
    INotificationSender notifications,
    IAuditWriter audit,
    IOutbox outbox,
    IClock clock)
{
    public static readonly TimeSpan ReadEvery = TimeSpan.FromHours(6);
    public static readonly TimeSpan KeepFor = TimeSpan.FromDays(730);
    public const int ReviewDueDays = 30;
    public const int MaxLinked = 20;
    public const int MaxNote = 2000;
    public const int MaxListed = 200;
    public const string NewChangesTemplate = "policies.regulator_changes_new";
    public const string Link = "/policies/regulator-changes";

    private async Task<bool> AvailableAsync(CancellationToken ct) =>
        await entitlements.IsEnabledAsync(Capabilities.PolicyCore, ct) && await entitlements.IsEnabledAsync(Capabilities.PolicyRegulatorFeeds, ct);

    private async Task DemandAsync(CancellationToken ct)
    {
        await entitlements.DemandAsync(Capabilities.PolicyRegulatorFeeds, ct);
        context.RequireUser();
        if (!(await authz.CoverageAsync(Permissions.PolicyManage, ct)).Any)
        {
            throw AppException.Denied();
        }
    }

    /// <summary>For one tenant: reads the feeds that are due, keeps new items, tells the officers, and deletes items after 2 years.</summary>
    public async Task<RegulatorRun> RunDueAsync(DateTimeOffset now, CancellationToken ct)
    {
        if (!await AvailableAsync(ct))
        {
            return new(0, 0, 0);
        }
        var purged = await session.ExecAsync("delete from policies.regulator_change where received_at < @cutoff", new { cutoff = now - KeepFor }, ct);
        int read = 0, added = 0;
        foreach (var feed in await feeds.ConnectionsAsync(ct))
        {
            if (await session.ScalarAsync<bool>("select exists(select 1 from policies.regulator_feed_read where connection_id = @id and read_at > @since)",
                    new { id = feed.Id, since = now - ReadEvery }, ct))
            {
                continue;
            }
            var result = await feeds.ReadAsync(feed.Id, ct);
            read++;
            await session.ExecAsync(
                """
                insert into policies.regulator_feed_read (tenant_id, connection_id, read_at, result) values (platform.current_tenant_id(), @id, @now, @result)
                on conflict (tenant_id, connection_id) do update set read_at = excluded.read_at, result = excluded.result
                """, new { id = feed.Id, now, result = result.Result }, ct);
            if (result.Result != TrackerResults.Ok)
            {
                continue;
            }
            var fresh = 0;
            foreach (var item in result.Items.Where(i => i.Published is not { } p || now - p < KeepFor))
            {
                fresh += await session.ExecAsync(
                    """
                    insert into policies.regulator_change (tenant_id, id, connection_id, source_name, item_id, title, link, published_at, summary, received_at)
                    values (platform.current_tenant_id(), @id, @connection, @source, @item, @title, @link, @published, @summary, @now)
                    on conflict (tenant_id, connection_id, item_id) do nothing
                    """,
                    new
                    {
                        id = Ids.New(), connection = feed.Id, source = feed.Name.Length > 200 ? feed.Name[..200] : feed.Name, item = item.Id, title = item.Title,
                        link = item.Link is { } l && l.StartsWith("https://", StringComparison.Ordinal) ? l : null, published = item.Published, summary = item.Summary, now,
                    }, ct);
            }
            added += fresh;
            if (fresh > 0)
            {
                var officers = await holders.TenantHoldersAsync(Permissions.PolicyManage, ct);
                if (officers.Count > 0)
                {
                    await notifications.SendAsync(new NotificationRequest(officers.Distinct().ToList(), NotificationCategories.DocumentUpdate, NewChangesTemplate, Link,
                        DedupeKey: $"policies.regulator:{feed.Id:N}:{now.UtcTicks}"), ct);
                }
            }
        }
        return new(read, added, purged);
    }

    /// <summary>Regulator changes (US10), newest first: new ones by default, or those with the given status.</summary>
    public async Task<RegulatorChangesView> ListAsync(string? status, CancellationToken ct)
    {
        if (!await entitlements.IsEnabledAsync(Capabilities.PolicyRegulatorFeeds, ct))
        {
            return new(false, [], []);
        }
        await DemandAsync(ct);
        if (status is not null && !RegulatorChangeStatuses.All.Contains(status))
        {
            var invalid = new ValidationErrors();
            invalid.Add("status", "validation.policies.regulator_status");
            invalid.ThrowIfAny();
        }
        var rows = await session.QueryAsync(
            $"select {Row.Columns} from policies.regulator_change where status = @status order by coalesce(published_at, received_at) desc, id limit @limit",
            new { status = status ?? RegulatorChangeStatuses.New, limit = MaxListed }, Row.Read, ct);
        var items = new List<RegulatorChangeView>();
        foreach (var row in rows)
        {
            items.Add(await ViewAsync(row, ct));
        }
        return new(true, await feeds.ConnectionsAsync(ct), items);
    }

    /// <summary>Marks a new change as not relevant (US10 scenario 2), keeping who decided and when.</summary>
    public async Task<RegulatorChangeView> DismissAsync(Guid id, RegulatorDecisionRequest request, CancellationToken ct)
    {
        await DemandAsync(ct);
        var row = await NewRowAsync(id, ct);
        var errors = new ValidationErrors();
        var note = Note(request.Note, errors);
        errors.ThrowIfAny();
        var now = clock.UtcNow;
        await session.ExecAsync("update policies.regulator_change set status = 'dismissed', note = @note, decided_by = @by, decided_at = @now where id = @id",
            new { id, note, by = context.RequireUser(), now }, ct);
        await audit.WriteAsync(new AuditEntry(AuditActions.PolicyRegulatorChangeDecided, "policy.regulator_change", id.ToString(),
            Changes: new { decision = RegulatorChangeStatuses.Dismissed, source = row.Source, item = row.ItemId }), ct);
        return await ViewAsync((await RowAsync(id, ct))!, ct);
    }

    /// <summary>
    /// Links a new change to policies the person manages and, when asked, starts their reviews (US10 scenario 3). A linked
    /// policy in force without an open review gets a review due in 30 days; one with an open review keeps it.
    /// </summary>
    public async Task<RegulatorChangeView> LinkAsync(Guid id, RegulatorLinkRequest request, CancellationToken ct)
    {
        await DemandAsync(ct);
        var row = await NewRowAsync(id, ct);
        var errors = new ValidationErrors();
        var ids = (request.PolicyIds ?? []).Distinct().ToList();
        if (ids.Count is 0 or > MaxLinked)
        {
            errors.Add("policyIds", "validation.policies.regulator_policies");
        }
        var note = Note(request.Note, errors);
        errors.ThrowIfAny();
        var policies = new List<PolicyRow>();
        foreach (var policyId in ids)
        {
            var policy = await register.RowAsync(policyId, true, ct);
            if (policy is null || !await register.ManagesAsync(policy, ct))
            {
                errors.Add("policyIds", "validation.policies.regulator_policies");
                errors.ThrowIfAny();
            }
            policies.Add(policy!);
        }
        var now = clock.UtcNow;
        var started = new List<Guid>();
        if (request.StartReviews == true)
        {
            foreach (var policy in policies.Where(p => p.Status == PolicyStatuses.Published && p.CurrentVersion is not null))
            {
                var due = now.AddDays(ReviewDueDays);
                var review = await session.ScalarAsync<Guid?>(
                    """
                    insert into policies.policy_review (tenant_id, id, policy_id, version, due_at, opened_at, regulator_change_id)
                    select platform.current_tenant_id(), @review, @policy, @version, @due, @now, @change
                    where not exists (select 1 from policies.policy_review where policy_id = @policy and completed_at is null)
                    on conflict do nothing
                    returning id
                    """, new { review = Ids.New(), policy = policy.Id, version = policy.CurrentVersion!.Value, due, now, change = id }, ct);
                if (review is not { } reviewId)
                {
                    continue;
                }
                started.Add(policy.Id);
                await outbox.EnqueueAsync(new PolicyReviewDue(policy.Id, policy.CurrentVersion.Value, due, policy.Owner), ct);
                await notifications.SendAsync(new NotificationRequest([policy.Owner], NotificationCategories.DocumentUpdate, PolicyReviews.ReviewDueTemplate,
                    PolicyReviews.ReviewsLink(policy.Id), DedupeKey: $"policies.review:{reviewId:N}:due"), ct);
            }
        }
        await session.ExecAsync(
            "update policies.regulator_change set status = 'actioned', note = @note, decided_by = @by, decided_at = @now, policy_ids = @ids where id = @id",
            new { id, note, by = context.RequireUser(), now, ids = ids.ToArray() }, ct);
        await audit.WriteAsync(new AuditEntry(AuditActions.PolicyRegulatorChangeDecided, "policy.regulator_change", id.ToString(),
            Changes: new { decision = RegulatorChangeStatuses.Actioned, source = row.Source, item = row.ItemId, policies = ids, reviews = started }), ct);
        return await ViewAsync((await RowAsync(id, ct))!, ct);
    }

    private static string? Note(string? value, ValidationErrors errors)
    {
        var note = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        if (note is { Length: > MaxNote })
        {
            errors.Add("note", "validation.policies.regulator_note");
        }
        return note;
    }

    private async Task<Row> NewRowAsync(Guid id, CancellationToken ct)
    {
        var row = await RowAsync(id, ct) ?? throw AppException.NotFound();
        if (row.Status != RegulatorChangeStatuses.New)
        {
            throw AppException.Conflict("conflict.policies.regulator_decided");
        }
        return row;
    }

    private Task<Row?> RowAsync(Guid id, CancellationToken ct) =>
        session.QuerySingleOrDefaultAsync($"select {Row.Columns} from policies.regulator_change where id = @id for update", new { id }, Row.Read, ct);

    private async Task<RegulatorChangeView> ViewAsync(Row r, CancellationToken ct)
    {
        var linked = new List<RegulatorLinkedPolicy>();
        if (r.PolicyIds.Length > 0)
        {
            var reviewed = (await session.QueryAsync("select policy_id from policies.policy_review where regulator_change_id = @id", new { id = r.Id }, x => x.GetGuid(0), ct))
                .ToHashSet();
            foreach (var policyId in r.PolicyIds)
            {
                if (await register.RowAsync(policyId, false, ct) is { } policy && (await register.ManagesAsync(policy, ct) || await register.ReadsPublishedAsync(policy, ct)))
                {
                    linked.Add(new RegulatorLinkedPolicy(await register.SummaryAsync(policy, ct), reviewed.Contains(policyId)));
                }
            }
        }
        return new RegulatorChangeView(r.Id, r.Source, r.Title, r.Link, r.PublishedAt, r.Summary, r.ReceivedAt, r.Status, r.Note,
            r.DecidedBy is { } by ? await register.PersonAsync(by, ct) : null, r.DecidedAt, linked);
    }

    private sealed record Row(Guid Id, string Source, string ItemId, string Title, string? Link, DateTimeOffset? PublishedAt, string? Summary, DateTimeOffset ReceivedAt,
        string Status, string? Note, Guid? DecidedBy, DateTimeOffset? DecidedAt, Guid[] PolicyIds)
    {
        public const string Columns = "id, source_name, item_id, title, link, published_at, summary, received_at, status, note, decided_by, decided_at, policy_ids";

        public static Row Read(Npgsql.NpgsqlDataReader r) => new(r.GetGuid(0), r.GetString(1), r.GetString(2), r.GetString(3), r.IsDBNull(4) ? null : r.GetString(4),
            r.IsDBNull(5) ? null : r.GetFieldValue<DateTimeOffset>(5), r.IsDBNull(6) ? null : r.GetString(6), r.GetFieldValue<DateTimeOffset>(7), r.GetString(8),
            r.IsDBNull(9) ? null : r.GetString(9), r.IsDBNull(10) ? null : r.GetGuid(10), r.IsDBNull(11) ? null : r.GetFieldValue<DateTimeOffset>(11), r.GetFieldValue<Guid[]>(12));
    }
}
