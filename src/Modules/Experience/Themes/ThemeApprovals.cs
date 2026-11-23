using System.Text.Json;
using TeamBeats.Audit.Contracts;
using TeamBeats.Authorization.Contracts;
using TeamBeats.Localization;
using TeamBeats.Modules.Notifications.Contracts;
using TeamBeats.Modules.Workflow.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Primitives;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.Experience.Themes;

public sealed record SubmitBrandRequest(string? Reason);

/// <summary>One token whose value differs between the version in force and a submission.</summary>
public sealed record TokenChange(string Token, string Change, TokenValue? Before, TokenValue? After);

/// <param name="Decide">The caller may approve or return it: they publish themes and did not submit it.</param>
public sealed record BrandSubmissionView(Guid Id, Guid BrandId, string Name, string Status, int BaseVersion, Guid SubmittedBy, DateTimeOffset SubmittedAt,
    string? Reason, Guid? DecidedBy, DateTimeOffset? DecidedAt, string? Comment, int? Version, IReadOnlyList<TokenChange> Changes,
    IReadOnlyList<string> AssetChanges, IReadOnlyList<ContrastFinding> Findings, bool Decide);

/// <param name="Publish">The caller publishes themes, so the draft can go live without approval.</param>
public sealed record BrandApprovalState(bool Publish, BrandSubmissionView? Pending, BrandSubmissionView? Last);

/// <summary>
/// Theme approval (spec 033 R2 T014, US2 scenario 3, FR-012). An editor who does not publish themes submits the brand's draft:
/// it is kept as sent and waits on the spec 024 engine, so people holding <c>theme.publish</c> find it in the spec 016 inbox
/// with the token changes against the version in force and the contrast findings. Approving publishes exactly what was sent
/// as the next version; returning sends it back with a comment. Until then employees see the version in force. The submitter
/// never decides their own change.
/// </summary>
public sealed class ThemeApprovals(
    IDbSession session,
    ITenantContext context,
    IAuthorizationService authz,
    IProcessEngine engine,
    ThemeService themes,
    INotificationSender notifications,
    ILocalizer localizer,
    IAuditWriter audit,
    IClock clock) : ITaskSource
{
    public const string ProcessKey = "experience.theme_approval:tenant";
    public const string Source = "theme_approval";
    public const string ApprovedTemplate = "experience.theme_approved";
    public const string ReturnedTemplate = "experience.theme_returned";

    public static string Link(Guid brandId) => $"/admin/branding?brand={brandId}";

    private sealed record Submission(Guid Id, Guid BrandId, BrandCandidate Candidate, int BaseVersion, string Status, string? Reason, Guid SubmittedBy,
        DateTimeOffset SubmittedAt, Guid? InstanceId, Guid? DecidedBy, DateTimeOffset? DecidedAt, string? Comment, int? Version);

    private const string Select =
        """
        select id, brand_id, name, tokens::text, assets::text, strict_contrast, base_version, status, reason, submitted_by, submitted_at, instance_id,
          decided_by, decided_at, comment, version
        from experience.brand_submission
        """;

    private static Submission Read(Npgsql.NpgsqlDataReader r) => new(r.GetGuid(0), r.GetGuid(1),
        new BrandCandidate(r.GetString(2), JsonSerializer.Deserialize<Dictionary<string, TokenValue>>(r.GetString(3), JsonDefaults.Options)!,
            JsonSerializer.Deserialize<Dictionary<string, string>>(r.GetString(4), JsonDefaults.Options)!, r.GetBoolean(5)),
        r.GetInt32(6), r.GetString(7), r.Get<string>("reason"), r.GetGuid(9), r.GetFieldValue<DateTimeOffset>(10), r.Get<Guid?>("instance_id"),
        r.Get<Guid?>("decided_by"), r.Get<DateTimeOffset?>("decided_at"), r.Get<string>("comment"), r.Get<int?>("version"));

    private Task<Submission?> PendingAsync(Guid brandId, bool forUpdate, CancellationToken ct) =>
        session.QuerySingleOrDefaultAsync(Select + " where brand_id = @brandId and status = 'pending'" + (forUpdate ? " for update" : ""), new { brandId }, Read, ct);

    private Task<Submission?> ByInstanceAsync(Guid instanceId, bool forUpdate, CancellationToken ct) =>
        session.QuerySingleOrDefaultAsync(Select + " where instance_id = @instanceId" + (forUpdate ? " for update" : ""), new { instanceId }, Read, ct);

    private async Task<bool> PublisherAsync(CancellationToken ct) => (await authz.CheckAsync(Permissions.ThemePublish, AuthzTarget.Tenant, ct)).Allowed;

    private async Task DemandReaderAsync(CancellationToken ct)
    {
        if (!(await authz.CheckAsync(Permissions.ThemeEdit, AuthzTarget.Tenant, ct)).Allowed && !await PublisherAsync(ct))
        {
            throw AppException.NotFound();
        }
    }

    public async Task<BrandApprovalState> StateAsync(Guid brandId, CancellationToken ct)
    {
        await DemandReaderAsync(ct);
        var brand = await themes.VersionInForceAsync(brandId, ct) ?? throw AppException.NotFound();
        var pending = await PendingAsync(brandId, false, ct);
        var last = await session.QuerySingleOrDefaultAsync(Select + " where brand_id = @brandId and status <> 'pending' order by submitted_at desc limit 1",
            new { brandId }, Read, ct);
        return new BrandApprovalState(await PublisherAsync(ct), pending is null ? null : await ViewAsync(pending, brand, ct),
            last is null ? null : await ViewAsync(last, brand, ct));
    }

    private async Task<BrandSubmissionView> ViewAsync(Submission s, BrandCandidate inForce, CancellationToken ct)
    {
        var decide = s.Status == "pending" && s.SubmittedBy != context.RequireUser() && await PublisherAsync(ct);
        var changes = s.Candidate.Tokens.Keys.Union(inForce.Tokens.Keys, StringComparer.Ordinal).Order(StringComparer.Ordinal)
            .Select(token => (token, before: inForce.Tokens.GetValueOrDefault(token), after: s.Candidate.Tokens.GetValueOrDefault(token)))
            .Where(x => !SameToken(x.before, x.after))
            .Select(x => new TokenChange(x.token, x.before is null ? "added" : x.after is null ? "removed" : "changed", x.before, x.after)).ToList();
        var assets = s.Candidate.Assets.Keys.Union(inForce.Assets.Keys, StringComparer.Ordinal).Order(StringComparer.Ordinal)
            .Where(slot => s.Candidate.Assets.GetValueOrDefault(slot) != inForce.Assets.GetValueOrDefault(slot)).ToList();
        return new BrandSubmissionView(s.Id, s.BrandId, s.Candidate.Name, s.Status, s.BaseVersion, s.SubmittedBy, s.SubmittedAt, s.Reason, s.DecidedBy,
            s.DecidedAt, s.Comment, s.Version, changes, assets, ThemeService.FindingsOf(s.Candidate), decide);
    }

    private static bool SameToken(TokenValue? a, TokenValue? b) =>
        a is null ? b is null : b is not null && JsonSerializer.Serialize(a, JsonDefaults.Options) == JsonSerializer.Serialize(b, JsonDefaults.Options);

    /// <summary>Submits the brand's draft (theme.edit). It must pass the checks a publish runs; one submission of a brand waits at a time.</summary>
    public async Task<BrandSubmissionView> SubmitAsync(Guid brandId, SubmitBrandRequest request, CancellationToken ct)
    {
        await authz.DemandAsync(Permissions.ThemeEdit, AuthzTarget.Tenant, ct);
        var brand = await themes.VersionInForceAsync(brandId, ct) ?? throw AppException.NotFound();
        var reason = string.IsNullOrWhiteSpace(request.Reason) ? null : request.Reason.Trim();
        if (reason is { Length: > ThemeService.MaxReasonLength })
        {
            throw AppException.Validation("reason", "validation.theme.reason_too_long");
        }
        if (await PendingAsync(brandId, true, ct) is not null)
        {
            throw AppException.Conflict("conflict.theme.in_approval");
        }
        var candidate = await themes.DraftCandidateAsync(brandId, ct);
        await themes.ThrowIfBlockedAsync(brandId, candidate, ct);
        var baseVersion = await themes.CurrentVersionOfAsync(brandId, ct);
        var user = context.RequireUser();
        var id = Ids.New();
        await engine.PublishRouteAsync(ProcessKey, RouteSources.ThemeRoute, new RouteSpec("theme approval",
            [new RouteStep("decision", "Theme decision", new CandidateRule(Permission: Permissions.ThemePublish))]), ct);
        await session.ExecAsync(
            """
            insert into experience.brand_submission (tenant_id, id, brand_id, name, tokens, assets, strict_contrast, base_version, status, reason, submitted_by, submitted_at)
            values (platform.current_tenant_id(), @id, @brandId, @name, @tokens, @assets, @strict, @baseVersion, 'pending', @reason, @user, @now)
            """, new { id, brandId, name = candidate.Name, tokens = Jsonb.From(candidate.Tokens), assets = Jsonb.From(candidate.Assets), strict = candidate.Strict,
                baseVersion, reason, user, now = clock.UtcNow }, ct);
        var instance = await engine.StartAsync(ProcessKey, id.ToString("N"), null, user, ct: ct);
        await session.ExecAsync("update experience.brand_submission set instance_id = @instance where id = @id", new { instance = instance.Id, id }, ct);
        await audit.WriteAsync(new AuditEntry(AuditActions.ThemeSubmitted, "brand", brandId.ToString(),
            Changes: new { brand = brandId, submission = id, baseVersion, reason }), ct);
        return await ViewAsync((await PendingAsync(brandId, false, ct))!, brand, ct);
    }

    /// <summary>Takes a waiting submission back (theme.edit); its task leaves the inbox and the draft stays.</summary>
    public async Task WithdrawAsync(Guid brandId, CancellationToken ct)
    {
        await authz.DemandAsync(Permissions.ThemeEdit, AuthzTarget.Tenant, ct);
        var pending = await PendingAsync(brandId, true, ct) ?? throw AppException.NotFound();
        await session.ExecAsync("update experience.brand_submission set status = 'withdrawn', decided_by = @user, decided_at = @now where id = @id",
            new { id = pending.Id, user = context.RequireUser(), now = clock.UtcNow }, ct);
        if (pending.InstanceId is { } instance && await engine.GetInstanceAsync(instance, ct) is { Status: "active" })
        {
            await engine.WithdrawAsync(instance, ct);
        }
        await audit.WriteAsync(new AuditEntry(AuditActions.ThemeReturned, "brand", brandId.ToString(),
            Changes: new { brand = brandId, submission = pending.Id, withdrawn = true }), ct);
    }

    // ---- The spec 016 task inbox ----

    public bool Handles(string definitionKey) => definitionKey == ProcessKey;

    async Task<TaskSubject?> ITaskSource.DescribeAsync(UserTaskInfo task, CancellationToken ct)
    {
        if (await ByInstanceAsync(task.InstanceId, false, ct) is not { Status: "pending" } submission)
        {
            return null;
        }
        var title = Locales.LaunchComplete.ToDictionary(l => l,
            l => localizer.Get("experience.task.theme_approval", l).Replace("{brand}", submission.Candidate.Name, StringComparison.Ordinal));
        return new TaskSubject(Source, title, Link(submission.BrandId) + "&task=" + task.Id, null, submission.SubmittedBy);
    }

    public async Task<bool> MayDecideAsync(UserTaskInfo task, Guid authority, CancellationToken ct = default) =>
        await ByInstanceAsync(task.InstanceId, false, ct) is { Status: "pending" } submission && authority != submission.SubmittedBy;

    async Task ITaskSource.DecidedAsync(UserTaskInfo task, UserTaskInfo done, CancellationToken ct)
    {
        if (await ByInstanceAsync(task.InstanceId, true, ct) is not { Status: "pending" } submission)
        {
            return;
        }
        var decider = done.CompletedBy ?? context.RequireUser();
        var now = clock.UtcNow;
        var comment = string.IsNullOrWhiteSpace(done.Comment) ? null : done.Comment.Trim();
        var vars = new Dictionary<string, string> { ["brand"] = submission.Candidate.Name, ["comment"] = comment ?? "" };
        if (done.Outcome == TaskOutcomes.Approve)
        {
            // Passing when sent is not enough: a scope may use the brand since, so the approver sees what blocks it.
            var version = await themes.PublishCandidateAsync(submission.BrandId, submission.Candidate, "approved", submission.Reason, null, decider, ct);
            await session.ExecAsync(
                "update experience.brand_submission set status = 'approved', decided_by = @decider, decided_at = @now, comment = @comment, version = @version where id = @id",
                new { id = submission.Id, decider, now, comment, version }, ct);
            await audit.WriteAsync(new AuditEntry(AuditActions.ThemeApproved, "brand", submission.BrandId.ToString(),
                Changes: new { brand = submission.BrandId, submission = submission.Id, version, comment }), ct);
            await notifications.SendAsync(new NotificationRequest([submission.SubmittedBy], NotificationCategories.RequestUpdate, ApprovedTemplate,
                Link(submission.BrandId), vars, DedupeKey: $"brand_submission:{submission.Id:N}:decided"), ct);
            return;
        }
        await session.ExecAsync(
            "update experience.brand_submission set status = 'returned', decided_by = @decider, decided_at = @now, comment = @comment where id = @id",
            new { id = submission.Id, decider, now, comment }, ct);
        await audit.WriteAsync(new AuditEntry(AuditActions.ThemeReturned, "brand", submission.BrandId.ToString(),
            Changes: new { brand = submission.BrandId, submission = submission.Id, outcome = done.Outcome, comment }), ct);
        await notifications.SendAsync(new NotificationRequest([submission.SubmittedBy], NotificationCategories.RequestUpdate, ReturnedTemplate,
            Link(submission.BrandId), vars, DedupeKey: $"brand_submission:{submission.Id:N}:decided"), ct);
    }
}
