using System.Text.Json;
using TeamBeats.Audit.Contracts;
using TeamBeats.Authorization.Contracts;
using TeamBeats.Entitlements.Contracts;
using TeamBeats.Localization;
using TeamBeats.Messaging;
using TeamBeats.Modules.Notifications.Contracts;
using TeamBeats.Modules.Workflow.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Primitives;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.Experience.Pages;

public sealed record SubmitPageRequest(string? Reason);

/// <summary>One placement that differs between the release in force and a submission (structural diff, US6 scenario 1).</summary>
/// <param name="Change">added, removed, moved (to another region), resized (span) or changed (properties or audience).</param>
public sealed record PlacementChange(string PlacementId, string ComponentRef, string Change, string? FromRegion, string? ToRegion);

/// <param name="BaseRelease">The release the submission is compared with; null for a page never published.</param>
public sealed record PageDiff(int? BaseRelease, string? FromTemplate, string ToTemplate, string? FromVariant, string ToVariant,
    IReadOnlyList<PlacementChange> Changes);

/// <param name="Decide">The caller may approve or return it: an approver of its route who did not submit it.</param>
public sealed record PageSubmissionView(Guid Id, Guid PageId, string Slug, string Status, int Revision, Guid SubmittedBy, DateTimeOffset SubmittedAt,
    string? Reason, Guid? DecidedBy, DateTimeOffset? DecidedAt, string? Comment, int? Release, PageDiff Diff, bool Decide);

/// <param name="Required">An approval route applies to the page, so it goes live only through a submission.</param>
/// <param name="Pending">The submission waiting for a decision, if any; <paramref name="Last"/> is the latest decided one.</param>
/// <param name="Impact">Whether the package shows an impact report before publishing or submitting (spec 032 R2 T024).</param>
public sealed record PageApprovalState(bool Required, PageSubmissionView? Pending, PageSubmissionView? Last, bool Impact = false);

/// <summary>
/// Page approval (spec 032 R2 T023, US6 scenario 1, FR-051). Where an approval route applies, an editor submits the draft:
/// the definition is kept as it was sent, and the route runs on the spec 024 engine so its approvers find the task in the
/// spec 016 inbox with a structural diff against the release in force and a preview of what was sent. The approver
/// approves, which publishes that definition as the next release, or returns it with a comment. The submitter never
/// decides their own change; people holding <c>page.approve</c> at the page's scope and the route's named approvers do.
/// </summary>
public sealed class PageApprovals(
    IDbSession session,
    ITenantContext context,
    IAuthorizationService authz,
    IEntitlementService entitlements,
    IProcessEngine engine,
    PageService pages,
    PageRoutes routes,
    INotificationSender notifications,
    ILocalizer localizer,
    IAuditWriter audit,
    IClock clock) : ITaskSource, IMessageHandler<UserTaskCreated>
{
    public const string KeyPrefix = "experience.page_approval:";
    public const string Source = "page_approval";
    public const string WaitingTemplate = "experience.page_waiting";
    public const string ApprovedTemplate = "experience.page_approved";
    public const string ReturnedTemplate = "experience.page_returned";
    public const int MaxReasonLength = 500;

    /// <summary>One process per route, so its named approvers decide; republishing the same route keeps its version.</summary>
    public static string ProcessKey(Guid routeId) => KeyPrefix + routeId.ToString("N");

    public static string Link(Guid pageId) => $"/page-approvals/{pageId}";

    private sealed record Submission(Guid Id, Guid PageId, Guid RouteId, int Revision, PageDefinition Definition, int? BaseRelease, string Status,
        string? Reason, Guid SubmittedBy, DateTimeOffset SubmittedAt, Guid? InstanceId, Guid? DecidedBy, DateTimeOffset? DecidedAt, string? Comment, int? Release);

    private const string SubmissionSelect =
        """
        select id, page_id, route_id, revision, definition::text, base_release, status, reason, submitted_by, submitted_at, instance_id,
          decided_by, decided_at, comment, release
        from experience.page_submission
        """;

    private static Submission Read(Npgsql.NpgsqlDataReader r) => new(r.GetGuid(0), r.GetGuid(1), r.GetGuid(2), r.GetInt32(3),
        JsonSerializer.Deserialize<PageDefinition>(r.GetString(4), JsonDefaults.Options)!, r.Get<int?>("base_release"), r.GetString(6), r.Get<string>("reason"),
        r.GetGuid(8), r.GetFieldValue<DateTimeOffset>(9), r.Get<Guid?>("instance_id"), r.Get<Guid?>("decided_by"), r.Get<DateTimeOffset?>("decided_at"),
        r.Get<string>("comment"), r.Get<int?>("release"));

    private static AuthzTarget Target(Guid? orgUnitId) => orgUnitId is { } u ? AuthzTarget.OrgUnit(u) : AuthzTarget.Tenant;

    private Task<Submission?> PendingAsync(Guid pageId, bool forUpdate, CancellationToken ct) =>
        session.QuerySingleOrDefaultAsync(SubmissionSelect + " where page_id = @pageId and status = 'pending'" + (forUpdate ? " for update" : ""),
            new { pageId }, Read, ct);

    private Task<Submission?> ByInstanceAsync(Guid instanceId, bool forUpdate, CancellationToken ct) =>
        session.QuerySingleOrDefaultAsync(SubmissionSelect + " where instance_id = @instanceId" + (forUpdate ? " for update" : ""), new { instanceId }, Read, ct);

    /// <summary>Whether <paramref name="user"/> may decide: holds <c>page.approve</c> at the page's scope or is a named approver, and did not submit.</summary>
    private async Task<bool> ApproverAsync(Submission submission, PageInfo page, Guid user, CancellationToken ct)
    {
        if (user == submission.SubmittedBy)
        {
            return false;
        }
        var named = await session.ScalarAsync<bool>("select exists (select 1 from experience.page_route where id = @id and @user = any(approver_ids))",
            new { id = submission.RouteId, user }, ct);
        return named || (await authz.CheckAsync(Permissions.PageApprove, Target(page.OrgUnitId), ct)).Allowed;
    }

    /// <summary>Whether a route applies to the page, and its waiting and last decided submissions, for an editor or an approver.</summary>
    public async Task<PageApprovalState> StateAsync(Guid pageId, CancellationToken ct)
    {
        await entitlements.DemandAsync(Capabilities.ExperiencePageBuilder, ct);
        var page = await VisibleAsync(pageId, ct);
        var required = await routes.ForPageAsync(page.OrgUnitId, ct) is not null;
        var pending = await PendingAsync(pageId, false, ct);
        var last = await session.QuerySingleOrDefaultAsync(SubmissionSelect + " where page_id = @pageId and status <> 'pending' order by submitted_at desc limit 1",
            new { pageId }, Read, ct);
        return new PageApprovalState(required, pending is null ? null : await ViewAsync(pending, page, ct), last is null ? null : await ViewAsync(last, page, ct),
            await entitlements.IsEnabledAsync(Capabilities.ExperienceApprovalRoutes, ct));
    }

    /// <summary>
    /// The page for someone who edits it or approves at its scope; a named approver sees it while a submission of it waits.
    /// Not found for anyone else.
    /// </summary>
    private async Task<PageInfo> VisibleAsync(Guid pageId, CancellationToken ct)
    {
        var page = await pages.FindAsync(pageId, ct) ?? throw AppException.NotFound();
        if ((await authz.CheckAsync(Permissions.PageEdit, Target(page.OrgUnitId), ct)).Allowed
            || (await authz.CheckAsync(Permissions.PageApprove, Target(page.OrgUnitId), ct)).Allowed)
        {
            return page;
        }
        if (await PendingAsync(pageId, false, ct) is { } pending
            && await session.ScalarAsync<bool>("select exists (select 1 from experience.page_route where id = @id and @user = any(approver_ids))",
                new { id = pending.RouteId, user = context.RequireUser() }, ct))
        {
            return page;
        }
        throw AppException.NotFound();
    }

    private async Task<PageSubmissionView> ViewAsync(Submission s, PageInfo page, CancellationToken ct)
    {
        var baseline = await pages.ReleasedAsync(s.PageId, s.BaseRelease, ct);
        var decide = s.Status == "pending" && await ApproverAsync(s, page, context.RequireUser(), ct);
        return new PageSubmissionView(s.Id, s.PageId, page.Slug, s.Status, s.Revision, s.SubmittedBy, s.SubmittedAt, s.Reason, s.DecidedBy, s.DecidedAt,
            s.Comment, s.Release, Diff(baseline, s.Definition, s.BaseRelease), decide);
    }

    /// <summary>Placement by placement, what the submission changes against the release in force; unchanged placements are left out.</summary>
    internal static PageDiff Diff(PageDefinition? before, PageDefinition after, int? baseRelease)
    {
        static Dictionary<string, (string Region, Placement Placement)> Index(PageDefinition? d) =>
            (d?.Regions ?? []).SelectMany(r => r.Placements.Select(p => (r.Id, p))).GroupBy(x => x.p.Id, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => (g.First().Id, g.First().p), StringComparer.Ordinal);
        var old = Index(before);
        var now = Index(after);
        var changes = new List<PlacementChange>();
        foreach (var region in after.Regions)
        {
            foreach (var p in region.Placements)
            {
                if (!old.TryGetValue(p.Id, out var was))
                {
                    changes.Add(new PlacementChange(p.Id, p.ComponentRef, "added", null, region.Id));
                }
                else if (was.Region != region.Id)
                {
                    changes.Add(new PlacementChange(p.Id, p.ComponentRef, "moved", was.Region, region.Id));
                }
                else if (was.Placement.ComponentRef != p.ComponentRef || !SameJson(was.Placement.Props, p.Props) || was.Placement.VisibilityRuleRef != p.VisibilityRuleRef)
                {
                    changes.Add(new PlacementChange(p.Id, p.ComponentRef, "changed", was.Region, region.Id));
                }
                else if (was.Placement.Span != p.Span)
                {
                    changes.Add(new PlacementChange(p.Id, p.ComponentRef, "resized", was.Region, region.Id));
                }
            }
        }
        changes.AddRange(old.Where(o => !now.ContainsKey(o.Key)).Select(o => new PlacementChange(o.Key, o.Value.Placement.ComponentRef, "removed", o.Value.Region, null)));
        return new PageDiff(baseRelease, before is null ? null : $"{before.Template.Id}@{before.Template.Version}", $"{after.Template.Id}@{after.Template.Version}",
            before?.Variant, after.Variant, changes);
    }

    private static bool SameJson(JsonElement? a, JsonElement? b) =>
        (a is null || a.Value.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null) && (b is null || b.Value.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
        || a is { } x && b is { } y && JsonElement.DeepEquals(x, y);

    /// <summary>Submits the draft for approval (US6 scenario 1). It must be valid for publishing; one submission of a page waits at a time.</summary>
    public async Task<PageSubmissionView> SubmitAsync(Guid pageId, SubmitPageRequest request, CancellationToken ct)
    {
        await entitlements.DemandAsync(Capabilities.ExperiencePageBuilder, ct);
        var page = await pages.EditableInfoAsync(pageId, Permissions.PageEdit, ct);
        var route = await routes.ForPageAsync(page.OrgUnitId, ct) ?? throw AppException.Conflict("conflict.page.no_route");
        var reason = string.IsNullOrWhiteSpace(request.Reason) ? null : request.Reason.Trim();
        if (reason is { Length: > MaxReasonLength })
        {
            throw AppException.Validation("reason", "validation.page.reason_too_long");
        }
        if (await PendingAsync(pageId, true, ct) is not null)
        {
            throw AppException.Conflict("conflict.page.in_approval");
        }
        var (revision, draft) = await pages.DraftOfAsync(pageId, ct);
        PageService.Block(await pages.PublishFindingsAsync(pageId, draft, ct));
        var user = context.RequireUser();
        var now = clock.UtcNow;
        var id = Ids.New();
        var key = ProcessKey(route.Id);
        await engine.PublishRouteAsync(key, RouteSources.PageRoute, new RouteSpec("page approval",
            [new RouteStep("decision", "Page decision", new CandidateRule(Users: route.ApproverIds, Permission: Permissions.PageApprove))]), ct);
        await session.ExecAsync(
            """
            insert into experience.page_submission (tenant_id, id, page_id, route_id, revision, definition, base_release, status, reason, submitted_by, submitted_at)
            values (platform.current_tenant_id(), @id, @pageId, @route, @revision, @definition, @base, 'pending', @reason, @user, @now)
            """, new { id, pageId, route = route.Id, revision, definition = Jsonb.From(draft), @base = page.CurrentRelease, reason, user, now }, ct);
        var instance = await engine.StartAsync(key, id.ToString("N"), page.OrgUnitId, user, ct: ct);
        await session.ExecAsync("update experience.page_submission set instance_id = @instance where id = @id", new { instance = instance.Id, id }, ct);
        await audit.WriteAsync(new AuditEntry(AuditActions.PageSubmitted, "page", pageId.ToString(), TargetOrgUnitId: page.OrgUnitId,
            Changes: new { submission = id, revision, baseRelease = page.CurrentRelease, route = route.Id, reason }), ct);
        return await ViewAsync((await PendingAsync(pageId, false, ct))!, page, ct);
    }

    /// <summary>The submitter, or someone who edits the page, takes a waiting submission back; its task leaves the inbox.</summary>
    public async Task WithdrawAsync(Guid pageId, CancellationToken ct)
    {
        await entitlements.DemandAsync(Capabilities.ExperiencePageBuilder, ct);
        var page = await pages.EditableInfoAsync(pageId, Permissions.PageEdit, ct);
        var pending = await PendingAsync(pageId, true, ct) ?? throw AppException.NotFound();
        await session.ExecAsync("update experience.page_submission set status = 'withdrawn', decided_by = @user, decided_at = @now where id = @id",
            new { id = pending.Id, user = context.RequireUser(), now = clock.UtcNow }, ct);
        if (pending.InstanceId is { } instance && await engine.GetInstanceAsync(instance, ct) is { Status: "active" })
        {
            await engine.WithdrawAsync(instance, ct);
        }
        await audit.WriteAsync(new AuditEntry(AuditActions.PageReturned, "page", pageId.ToString(), TargetOrgUnitId: page.OrgUnitId,
            Changes: new { submission = pending.Id, withdrawn = true }), ct);
    }

    /// <summary>The waiting submission rendered as it would go live, for its approver (the visual side of the diff).</summary>
    public async Task<RenderedPage> PreviewAsync(Guid pageId, string? previewLocale, CancellationToken ct)
    {
        await entitlements.DemandAsync(Capabilities.ExperiencePageBuilder, ct);
        var page = await VisibleAsync(pageId, ct);
        var pending = await PendingAsync(pageId, false, ct) ?? throw AppException.NotFound();
        return await pages.PreviewDefinitionAsync(page, pending.Definition, previewLocale, ct);
    }

    // ---- The spec 016 task inbox ----

    public bool Handles(string definitionKey) => definitionKey.StartsWith(KeyPrefix, StringComparison.Ordinal);

    async Task<TaskSubject?> ITaskSource.DescribeAsync(UserTaskInfo task, CancellationToken ct)
    {
        if (await ByInstanceAsync(task.InstanceId, false, ct) is not { Status: "pending" } submission || await pages.FindAsync(submission.PageId, ct) is not { } page)
        {
            return null;
        }
        var title = Locales.LaunchComplete.ToDictionary(l => l, l => localizer.Get("experience.task.page_approval", l).Replace("{page}", page.Slug, StringComparison.Ordinal));
        return new TaskSubject(Source, title, Link(page.Id) + "?task=" + task.Id, page.OrgUnitId, submission.SubmittedBy);
    }

    public async Task<bool> MayDecideAsync(UserTaskInfo task, Guid authority, CancellationToken ct = default) =>
        await ByInstanceAsync(task.InstanceId, false, ct) is { Status: "pending" } submission && authority != submission.SubmittedBy;

    async Task ITaskSource.DecidedAsync(UserTaskInfo task, UserTaskInfo done, CancellationToken ct)
    {
        if (await ByInstanceAsync(task.InstanceId, true, ct) is not { Status: "pending" } submission || await pages.FindAsync(submission.PageId, ct) is not { } page)
        {
            return;
        }
        var decider = done.CompletedBy ?? context.RequireUser();
        var now = clock.UtcNow;
        var comment = string.IsNullOrWhiteSpace(done.Comment) ? null : done.Comment.Trim();
        if (done.Outcome == TaskOutcomes.Approve)
        {
            // Valid when sent is not enough: a component may have been turned off since, so the approver sees what blocks it.
            PageService.Block(await pages.PublishFindingsAsync(page.Id, submission.Definition, ct));
            var release = await pages.PublishApprovedAsync(page, submission.Definition, submission.Reason, submission.Id, ct);
            await session.ExecAsync(
                "update experience.page_submission set status = 'approved', decided_by = @decider, decided_at = @now, comment = @comment, release = @release where id = @id",
                new { id = submission.Id, decider, now, comment, release = release.Release }, ct);
            await audit.WriteAsync(new AuditEntry(AuditActions.PageApproved, "page", page.Id.ToString(), TargetOrgUnitId: page.OrgUnitId,
                Changes: new { submission = submission.Id, release = release.Release, comment }), ct);
            await notifications.SendAsync(new NotificationRequest([submission.SubmittedBy], NotificationCategories.RequestUpdate, ApprovedTemplate, Link(page.Id),
                new Dictionary<string, string> { ["page"] = page.Slug }, DedupeKey: $"page_submission:{submission.Id:N}:decided"), ct);
            return;
        }
        await session.ExecAsync(
            "update experience.page_submission set status = 'returned', decided_by = @decider, decided_at = @now, comment = @comment where id = @id",
            new { id = submission.Id, decider, now, comment }, ct);
        await audit.WriteAsync(new AuditEntry(AuditActions.PageReturned, "page", page.Id.ToString(), TargetOrgUnitId: page.OrgUnitId,
            Changes: new { submission = submission.Id, outcome = done.Outcome, comment }), ct);
        await notifications.SendAsync(new NotificationRequest([submission.SubmittedBy], NotificationCategories.RequestUpdate, ReturnedTemplate, Link(page.Id),
            new Dictionary<string, string> { ["page"] = page.Slug, ["comment"] = comment ?? "" }, DedupeKey: $"page_submission:{submission.Id:N}:decided"), ct);
    }

    /// <summary>The route's named approvers hear that a page waits; people with page.approve find it in their inbox.</summary>
    public async Task HandleAsync(UserTaskCreated message, CancellationToken ct)
    {
        if (!Handles(message.DefinitionKey) || await ByInstanceAsync(message.InstanceId, false, ct) is not { Status: "pending" } submission
            || await pages.FindAsync(submission.PageId, ct) is not { } page)
        {
            return;
        }
        var named = await session.ScalarAsync<Guid[]?>("select approver_ids from experience.page_route where id = @id", new { id = submission.RouteId }, ct) ?? [];
        var to = named.Where(a => a != submission.SubmittedBy).ToList();
        if (to.Count == 0)
        {
            return;
        }
        await notifications.SendAsync(new NotificationRequest(to, NotificationCategories.ApprovalWaiting, WaitingTemplate, Link(page.Id) + "?task=" + message.TaskId,
            new Dictionary<string, string> { ["page"] = page.Slug }, DedupeKey: $"page_submission:{submission.Id:N}:waiting"), ct);
    }
}
