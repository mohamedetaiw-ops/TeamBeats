using System.Security.Cryptography;
using System.Text;
using TeamBeats.Audit.Contracts;
using TeamBeats.Authorization.Contracts;
using TeamBeats.Messaging;
using TeamBeats.Modules.Notifications.Contracts;
using TeamBeats.Modules.Workflow.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Primitives;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.Content;

/// <summary>One line of an item's review history: submitted, approved, returned with a comment, withdrawn, schedule missed.</summary>
public sealed record ReviewEvent(int Version, string Event, ContentPerson? Actor, ContentPerson? OnBehalfOf, string? Comment, DateTimeOffset At);

/// <summary>
/// Route definitions and review history of spec 009 FR-003. Each site, type and set of deciders compiles to its own
/// engine definition, keyed by a hash of the step, so a change of approvers takes effect on the next submission and
/// running reviews keep the route they started on.
/// </summary>
public sealed class ContentReviews(IDbSession session, IProcessEngine engine, IClock clock)
{
    public const string KeyPrefix = "content.";

    /// <summary>The item a review instance belongs to: its business key starts with the item ID.</summary>
    internal static Guid? ItemIdOf(string definitionKey, string businessKey) =>
        definitionKey.StartsWith(KeyPrefix, StringComparison.Ordinal) && businessKey.Length >= 32 && Guid.TryParseExact(businessKey[..32], "N", out var id) ? id : null;

    /// <summary>
    /// The named approvers other than the writers, or the site's publishers (<c>content.publish</c> at the site's unit)
    /// when that leaves nobody (US1 scenario 6).
    /// </summary>
    internal static CandidateRule Candidates(IReadOnlyList<Guid>? approvers, IReadOnlyCollection<Guid> writers)
    {
        var named = (approvers ?? []).Where(a => !writers.Contains(a)).Distinct().Order().ToList();
        return named.Count > 0 ? new CandidateRule(Users: named) : new CandidateRule(Permission: Permissions.ContentPublish);
    }

    internal async Task<string> RouteKeyAsync(SiteRow site, string type, ContentRoute route, IReadOnlyCollection<Guid> writers, CancellationToken ct)
    {
        // One step keeps the shape of R1 routes, so their definitions are reused; each further step adds its own part.
        var steps = route.StepApprovers().Select((approvers, i) => new RouteStep(ContentRoute.StepId(i), ContentRoute.StepId(i), Candidates(approvers, writers))).ToList();
        var shape = string.Join("|", steps.Select(s => s.Candidates.Users is { } users ? "u:" + string.Join(",", users) : "p:" + s.Candidates.Permission));
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(shape)))[..12];
        var key = $"{KeyPrefix}{site.Id:N}.{type}.{hash}";
        if (await engine.GetDefinitionAsync(key, null, ct) is null)
        {
            await engine.PublishRouteAsync(key, RouteSources.ContentRoute, new RouteSpec($"{type} {site.Id:N}", steps), ct);
        }
        return key;
    }

    internal Task EventAsync(Guid item, int version, string name, Guid? actor, Guid? onBehalfOf, string? comment, CancellationToken ct) =>
        session.ExecAsync(
            """
            insert into content.item_event (tenant_id, id, item_id, version, event, actor_user_id, on_behalf_of, comment, at)
            values (platform.current_tenant_id(), @id, @item, @version, @name, @actor, @onBehalfOf, @comment, @now)
            """, new { id = Ids.New(), item, version, name, actor, onBehalfOf, comment, now = clock.UtcNow }, ct);

    /// <summary>Ends the review on the item: a draft goes back to draft, a live item stays live with its draft.</summary>
    internal Task CloseAsync(ItemRow item, CancellationToken ct) =>
        session.ExecAsync(
            """
            update content.content_item set status = case when status = 'in_review' then 'draft' else status end, review_instance_id = null,
              publish_at = null, updated_at = @now, row_version = row_version + 1
            where id = @id
            """, new { id = item.Id, now = clock.UtcNow }, ct);

    internal Task<List<(int Version, string Event, Guid? Actor, Guid? OnBehalfOf, string? Comment, DateTimeOffset At)>> HistoryAsync(Guid item, CancellationToken ct) =>
        session.QueryAsync("select version, event, actor_user_id, on_behalf_of, comment, at from content.item_event where item_id = @item order by at, id",
            new { item }, r => (r.GetInt32(0), r.GetString(1), r.Get<Guid?>("actor_user_id"), r.Get<Guid?>("on_behalf_of"), r.Get<string?>("comment"),
                r.GetFieldValue<DateTimeOffset>(5)), ct);
}

/// <summary>
/// Content reviews in the spec 016 task inbox (US1 scenarios 1 to 3). The inbox lists the review with the draft's
/// title; on approval the version is released at the wanted time, or now when that time has passed, and the person who
/// submitted it is told the schedule was missed; on return or reject the draft goes back to its writers with the
/// comment. Named approvers are told a review waits; publishers deciding by permission see the inbox badge.
/// </summary>
public sealed class ContentTaskSource(
    IDbSession session,
    ITenantContext context,
    IProcessEngine engine,
    Items items,
    Sites sites,
    ContentReviews reviews,
    IAuditWriter audit,
    INotificationSender notifications,
    IClock clock,
    ContentGovernance governance) : ITaskSource, IMessageHandler<UserTaskCreated>
{
    public const string Waiting = "content.review.waiting";
    public const string Returned = "content.review.returned";
    public const string Missed = "content.review.schedule_missed";

    public static string Link(Guid item) => $"/content/items/{item}";

    public bool Handles(string definitionKey) => definitionKey.StartsWith(ContentReviews.KeyPrefix, StringComparison.Ordinal);

    private async Task<(ItemRow Item, SiteRow Site)?> WaitingAsync(UserTaskInfo task, bool forUpdate, CancellationToken ct)
    {
        if (ContentReviews.ItemIdOf(task.DefinitionKey, task.BusinessKey) is not { } id || await items.RowAsync(id, forUpdate, ct) is not { } item
            || item.ReviewInstanceId != task.InstanceId)
        {
            return null;
        }
        return (item, (await sites.RowAsync(item.SiteId, false, ct))!);
    }

    public async Task<TaskSubject?> DescribeAsync(UserTaskInfo task, CancellationToken ct = default)
    {
        if (ContentGovernance.Owns(task.DefinitionKey))
        {
            return await governance.DescribeAsync(task, ct);
        }
        if (await WaitingAsync(task, false, ct) is not var (item, site) || item.DraftVersion is not { } number)
        {
            return null;
        }
        var version = await items.VersionAsync(item.Id, number, ct);
        var title = version?.Variants.ToDictionary(v => v.Key, v => v.Value.Title) ?? [];
        return new TaskSubject("content_review", title, Link(item.Id), site.OrgUnitId, item.SubmittedBy);
    }

    /// <summary>The engine already keeps writers out; this holds the line again for the person whose authority is used.</summary>
    public async Task<bool> MayDecideAsync(UserTaskInfo task, Guid authority, CancellationToken ct = default)
    {
        if (ContentGovernance.Owns(task.DefinitionKey))
        {
            return await governance.MayDecideAsync(task, authority, ct);
        }
        if (await WaitingAsync(task, false, ct) is not var (item, _) || item.DraftVersion is not { } number)
        {
            return false;
        }
        var author = (await items.VersionAsync(item.Id, number, ct))?.Author;
        return authority != item.SubmittedBy && authority != item.CreatedBy && authority != author;
    }

    public async Task DecidedAsync(UserTaskInfo task, UserTaskInfo done, CancellationToken ct = default)
    {
        if (ContentGovernance.Owns(task.DefinitionKey))
        {
            await governance.DecidedAsync(task, done, ct);
            return;
        }
        if (await WaitingAsync(task, true, ct) is not var (item, site) || item.DraftVersion is not { } version)
        {
            return;
        }
        var instance = await engine.GetInstanceAsync(task.InstanceId, ct);
        var decider = done.CompletedBy ?? context.RequireUser();
        if (instance is not { Status: "completed" })
        {
            // A step of a multi-step route was approved and the next one is open: the history keeps who passed it on.
            await reviews.EventAsync(item.Id, version, "step_approved", decider, done.OnBehalfOf, done.Comment, ct);
            return;
        }
        if (instance.Outcome == InstanceOutcomes.Approved)
        {
            await reviews.EventAsync(item.Id, version, "approved", decider, done.OnBehalfOf, done.Comment, ct);
            await session.ExecAsync("update content.content_version set approver_user_id = @decider where item_id = @id and number = @version",
                new { id = item.Id, version, decider }, ct);
            await session.ExecAsync("update content.content_item set review_instance_id = null where id = @id", new { id = item.Id }, ct);
            await audit.WriteAsync(new AuditEntry(AuditActions.ContentApproved, "content_item", item.Id.ToString(), TargetOrgUnitId: site.OrgUnitId,
                Changes: new { version, onBehalfOf = done.OnBehalfOf }), ct);
            var now = clock.UtcNow;
            var wanted = item.PublishAt;
            if (wanted is { } at && at <= now)
            {
                // Approval came after the wanted time: publish now and tell the person who submitted (US1 scenario 3).
                await reviews.EventAsync(item.Id, version, "schedule_missed", null, null, null, ct);
                await TellAsync(item.SubmittedBy is { } s ? [s] : [], Missed, item.Id, $"missed:{instance.Id:N}", ct);
                wanted = null;
            }
            await items.ReleaseAsync(item with { ReviewInstanceId = null }, site, version, wanted, item.ExpiresAt, ct);
            return;
        }
        await reviews.EventAsync(item.Id, version, "returned", decider, done.OnBehalfOf, done.Comment, ct);
        await reviews.CloseAsync(item, ct);
        await audit.WriteAsync(new AuditEntry(AuditActions.ContentReturned, "content_item", item.Id.ToString(), TargetOrgUnitId: site.OrgUnitId,
            Changes: new { version, outcome = instance.Outcome, comment = done.Comment is not null }), ct);
        var author = (await items.VersionAsync(item.Id, version, ct))?.Author;
        await TellAsync(new[] { item.SubmittedBy, author }.OfType<Guid>().Distinct().ToArray(), Returned, item.Id, $"returned:{instance.Id:N}", ct);
    }

    private async Task TellAsync(Guid[] recipients, string template, Guid item, string dedupe, CancellationToken ct)
    {
        if (recipients.Length > 0)
        {
            // No title: the item's text stays behind its read check.
            await notifications.SendAsync(new NotificationRequest(recipients, NotificationCategories.RequestUpdate, template, Link(item),
                DedupeKey: $"content:{item:N}:{dedupe}"), ct);
        }
    }

    public async Task HandleAsync(UserTaskCreated message, CancellationToken ct)
    {
        if (ContentGovernance.Owns(message.DefinitionKey))
        {
            await governance.TaskCreatedAsync(message, ct);
            return;
        }
        if (!Handles(message.DefinitionKey) || ContentReviews.ItemIdOf(message.DefinitionKey, message.BusinessKey) is not { } id
            || await items.RowAsync(id, false, ct) is not { } item || item.ReviewInstanceId != message.InstanceId)
        {
            return;
        }
        var site = (await sites.RowAsync(item.SiteId, false, ct))!;
        var author = item.DraftVersion is { } n ? (await items.VersionAsync(id, n, ct))?.Author : null;
        var writers = new[] { item.SubmittedBy, author, item.CreatedBy }.OfType<Guid>().ToArray();
        var steps = site.RouteOf(item.Type).StepApprovers();
        if (ContentRoute.StepIndex(message.ElementId) is not { } step || step >= steps.Count
            || ContentReviews.Candidates(steps[step], writers).Users is not { Count: > 0 } named)
        {
            return;
        }
        await notifications.SendAsync(new NotificationRequest(named.ToArray(), NotificationCategories.ApprovalWaiting, Waiting, Link(id) + "?task=" + message.TaskId,
            DedupeKey: "task:" + message.TaskId.ToString("N")), ct);
    }
}
