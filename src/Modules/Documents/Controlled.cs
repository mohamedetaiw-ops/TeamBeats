using System.Security.Cryptography;
using System.Text;
using TeamBeats.Audit.Contracts;
using TeamBeats.Authorization.Contracts;
using TeamBeats.Entitlements.Contracts;
using TeamBeats.Messaging;
using TeamBeats.Modules.Documents.Contracts;
using TeamBeats.Modules.Notifications.Contracts;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Modules.Workflow.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Primitives;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.Documents;

public sealed record ReleaseRequest(DateOnly? EffectiveOn, string? Note);

public sealed record DocumentReviewRequest(string? Action, DateOnly? NextReviewOn);

/// <summary>One version with what a banner needs: whether it is superseded, and the version in force instead.</summary>
public sealed record VersionDetailView(VersionView Version, bool Superseded, VersionView? Effective, string? Number);

public sealed record ReadConfirmationReport(Guid VersionId, string Label, int Requested, int Confirmed, IReadOnlyList<Guid> Pending);

public sealed record ReviewItem(Guid Id, string Name, string? Number, Guid OwnerId, DateTimeOffset ReviewDueAt, bool Overdue, bool TaskOpen);

public static class DocumentReviewActions
{
    public const string Confirm = "confirm";
    public const string Revise = "revise";
    public const string Withdraw = "withdraw";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal) { Confirm, Revise, Withdraw };
}

/// <summary>
/// Controlled documents (spec 011 US3, FR-007, FR-008). The owner or a contributor submits the current draft for release;
/// the type's route runs on the process engine with the author and the submitter kept out (no self-approval). On
/// approval the draft becomes the next major version with its effective date: at once, or on the date, when the
/// version in force becomes superseded. Each effective version sets the next review date; the owner's review task opens
/// 30 days before it with confirm, revise and withdraw. Read confirmations follow each new major version.
/// </summary>
public sealed class DocumentReleases(
    IDbSession session,
    ITenantContext context,
    DocumentFiles files,
    Libraries libraries,
    IProcessEngine engine,
    IEntitlementService entitlements,
    IAuthorizationProvisioning acl,
    ITenantSettingsReader tenantSettings,
    INotificationSender notifications,
    IAuditWriter audit,
    IOutbox outbox,
    IClock clock)
{
    public const string ReleasePrefix = "documents.release.";
    public const string ReviewPrefix = "documents.review.";
    public static readonly TimeSpan ReviewWindow = TimeSpan.FromDays(30);

    public const string ReleaseWaiting = "documents.release_waiting";
    public const string ReleaseReturned = "documents.release_returned";
    public const string ReleaseApproved = "documents.release_approved";
    public const string ReviewWaiting = "documents.review_waiting";
    public const string ReviewOverdue = "documents.review_overdue";

    public static string Link(Guid id) => $"/documents/{id}";

    /// <summary>The document an instance belongs to: its business key starts with the document ID.</summary>
    internal static Guid? DocumentIdOf(string businessKey) =>
        businessKey.Length >= 32 && Guid.TryParseExact(businessKey[..32], "N", out var id) ? id : null;

    internal static bool IsRelease(string definitionKey) => definitionKey.StartsWith(ReleasePrefix, StringComparison.Ordinal);

    internal static bool IsReview(string definitionKey) => definitionKey.StartsWith(ReviewPrefix, StringComparison.Ordinal);

    internal static CandidateRule Candidates(ApprovalStepSetting step) =>
        step.LibraryManagers ? new CandidateRule(Permission: Permissions.DocManage) : new CandidateRule(Users: step.Approvers);

    /// <summary>One engine definition per type and set of steps, so a change of approvers applies to the next release only.</summary>
    private async Task<string> RouteKeyAsync(Guid typeId, ControlledSettings settings, CancellationToken ct)
    {
        var steps = settings.Steps ?? [];
        var shape = string.Join(";", steps.Select(s => s.LibraryManagers ? "m" : "u:" + string.Join(",", (s.Approvers ?? []).Order())));
        var key = $"{ReleasePrefix}{typeId:N}.{Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(shape)))[..12]}";
        if (await engine.GetDefinitionAsync(key, null, ct) is null)
        {
            await engine.PublishRouteAsync(key, RouteSources.DocumentRoute,
                new RouteSpec($"document release {typeId:N}", steps.Select((s, i) => new RouteStep($"s{i}", s.Names?.GetValueOrDefault("en") ?? $"step {i + 1}", Candidates(s))).ToList()), ct);
        }
        return key;
    }

    private async Task<(LibraryRow Library, DocumentFiles.DocRow Doc, DocRights Rights, ControlledSettings Settings)> ControlledAsync(Guid id, bool forUpdate, CancellationToken ct)
    {
        var (library, doc, rights) = await files.VisibleAsync(id, ct, forUpdate);
        var settings = await libraries.ControlledOfAsync(doc.TypeId, ct) ?? throw AppException.Conflict("conflict.documents.not_controlled");
        await entitlements.DemandAsync(Capabilities.DocsControlled, ct);
        return (library, doc, rights, settings);
    }

    /// <summary>The start of a day in the tenant's time zone.</summary>
    private async Task<DateTimeOffset> StartOfAsync(DateOnly day, CancellationToken ct)
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById((await tenantSettings.GetLocaleSettingsAsync(ct)).TimeZone);
        return new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(day.ToDateTime(TimeOnly.MinValue), zone), TimeSpan.Zero);
    }

    private async Task<DateOnly> TodayAsync(CancellationToken ct)
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById((await tenantSettings.GetLocaleSettingsAsync(ct)).TimeZone);
        return DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(clock.UtcNow.UtcDateTime, zone));
    }

    // ---- Release ----

    /// <summary>Submits the current draft for release (US3 scenario 1). It may not change while approvers look at it.</summary>
    public async Task<DocumentView> SubmitAsync(Guid id, ReleaseRequest request, CancellationToken ct)
    {
        var user = context.RequireUser();
        var (library, doc, rights, settings) = await ControlledAsync(id, forUpdate: true, ct);
        if (!rights.Contribute)
        {
            throw AppException.Denied();
        }
        if (!doc.CheckedIn)
        {
            throw AppException.Conflict("conflict.documents.not_checked_in");
        }
        if (doc.CheckedOutBy is not null)
        {
            throw AppException.Conflict("conflict.documents.check_in_first");
        }
        if (doc.ReleaseInstance is not null)
        {
            throw AppException.Conflict("conflict.documents.in_release");
        }
        var current = await files.VersionAsync(doc.Current, ct);
        if (current is not { State: VersionStates.Draft })
        {
            throw AppException.Conflict("conflict.documents.not_draft");
        }
        var note = request.Note?.Trim();
        if (note is { Length: > DocumentVersions.MaxNoteLength })
        {
            throw AppException.Validation("note", "validation.documents.note");
        }
        DateTimeOffset? effectiveFrom = null;
        if (request.EffectiveOn is { } day)
        {
            if (day < await TodayAsync(ct))
            {
                throw AppException.Validation("effectiveOn", "validation.documents.effective_on");
            }
            effectiveFrom = await StartOfAsync(day, ct);
        }
        var number = doc.Number ?? await libraries.NextNumberAsync(settings, ct);
        var key = await RouteKeyAsync(doc.TypeId!.Value, settings, ct);
        var instance = await engine.StartAsync(key, $"{id:N}{Ids.New():N}", library.OrgUnitId, user, [current.AuthorUserId], user, ct);
        await session.ExecAsync(
            """
            update documents.document set release_instance_id = @instance, release_version_id = @version, release_effective_from = @effectiveFrom,
              release_submitted_by = @user, number = @number, updated_at = @now, row_version = row_version + 1
            where id = @id
            """, new { id, instance = instance.Id, version = current.Id, effectiveFrom, user, number, now = clock.UtcNow }, ct);
        await session.ExecAsync("update documents.document_version set state = 'in_review', note = coalesce(@note, note) where id = @version",
            new { version = current.Id, note = string.IsNullOrEmpty(note) ? null : note }, ct);
        await audit.WriteAsync(new AuditEntry(AuditActions.DocReleaseSubmitted, Securables.Document, id.ToString(), TargetOrgUnitId: library.OrgUnitId,
            Changes: new { version = current.Id, label = current.Label, effectiveFrom, instance = instance.Id }), ct);
        return await files.GetAsync(id, ct);
    }

    /// <summary>The person who submitted, or a library manager, takes the release back; the version is a draft again.</summary>
    public async Task<DocumentView> WithdrawReleaseAsync(Guid id, CancellationToken ct)
    {
        var user = context.RequireUser();
        var (library, doc, rights, _) = await ControlledAsync(id, forUpdate: true, ct);
        if (doc.ReleaseInstance is not { } instance)
        {
            throw AppException.Conflict("conflict.documents.no_release");
        }
        var submitter = await session.ScalarAsync<Guid?>("select release_submitted_by from documents.document where id = @id", new { id }, ct);
        if (submitter != user && !rights.Manage)
        {
            throw AppException.Denied();
        }
        await engine.WithdrawAsync(instance, ct);
        await CloseReleaseAsync(doc, ct);
        await audit.WriteAsync(new AuditEntry(AuditActions.DocReleaseWithdrawn, Securables.Document, id.ToString(), TargetOrgUnitId: library.OrgUnitId,
            Changes: new { version = doc.ReleaseVersion, instance }), ct);
        return await files.GetAsync(id, ct);
    }

    private async Task CloseReleaseAsync(DocumentFiles.DocRow doc, CancellationToken ct)
    {
        await session.ExecAsync("update documents.document_version set state = 'draft' where id = @version and state = 'in_review'", new { version = doc.ReleaseVersion }, ct);
        await session.ExecAsync(
            """
            update documents.document set release_instance_id = null, release_version_id = null, release_effective_from = null, release_submitted_by = null,
              updated_at = @now, row_version = row_version + 1
            where id = @id
            """, new { id = doc.Id, now = clock.UtcNow }, ct);
    }

    /// <summary>All steps approved (US3 scenario 2): the next major version, effective now or waiting for its date.</summary>
    internal async Task ApprovedAsync(LibraryRow library, DocumentFiles.DocRow doc, Guid decider, Guid? onBehalfOf, CancellationToken ct)
    {
        var versionId = doc.ReleaseVersion!.Value;
        var release = await session.QuerySingleOrDefaultAsync("select release_effective_from, release_submitted_by from documents.document where id = @id",
            new { id = doc.Id }, r => (From: r.Get<DateTimeOffset?>("release_effective_from"), By: r.Get<Guid?>("release_submitted_by")), ct);
        var version = (await files.VersionAsync(versionId, ct))!;
        var effective = await files.VersionAsync(doc.Effective, ct);
        var major = Math.Max(effective?.Major ?? 0, version.Major) + 1;
        var now = clock.UtcNow;
        var from = release.From is { } at && at > now ? at : now;
        await session.ExecAsync(
            "update documents.document_version set major = @major, minor = 0, state = 'approved', approver_user_id = @decider, effective_from = @from where id = @versionId",
            new { versionId, major, decider, from }, ct);
        await session.ExecAsync(
            """
            update documents.document set current_version_id = @versionId, scheduled_version_id = @versionId, release_instance_id = null, release_version_id = null,
              release_effective_from = null, release_submitted_by = null, updated_at = @now, row_version = row_version + 1
            where id = @id
            """, new { id = doc.Id, versionId, now }, ct);
        await audit.WriteAsync(new AuditEntry(AuditActions.DocReleaseApproved, Securables.Document, doc.Id.ToString(), TargetOrgUnitId: library.OrgUnitId,
            Changes: new { version = versionId, label = VersionLabels.Of(major, 0), effectiveFrom = from, approver = decider, onBehalfOf }), ct);
        if (release.By is { } submitter)
        {
            await notifications.SendAsync(new NotificationRequest([submitter], NotificationCategories.DocumentUpdate, ReleaseApproved, Link(doc.Id),
                DedupeKey: $"documents.release:{versionId:N}:approved"), ct);
        }
        if (from <= now)
        {
            await MakeEffectiveAsync(doc.Id, now, ct);
        }
    }

    /// <summary>Returned or rejected: the version is a draft again and the submitter and author hear it with the comment in the history.</summary>
    internal async Task ReturnedAsync(LibraryRow library, DocumentFiles.DocRow doc, string? outcome, Guid decider, CancellationToken ct)
    {
        var submitter = await session.ScalarAsync<Guid?>("select release_submitted_by from documents.document where id = @id", new { id = doc.Id }, ct);
        var author = (await files.VersionAsync(doc.ReleaseVersion, ct))?.AuthorUserId;
        await CloseReleaseAsync(doc, ct);
        await audit.WriteAsync(new AuditEntry(AuditActions.DocReleaseReturned, Securables.Document, doc.Id.ToString(), TargetOrgUnitId: library.OrgUnitId,
            Changes: new { version = doc.ReleaseVersion, outcome, decider }), ct);
        var recipients = new[] { submitter, author }.OfType<Guid>().Distinct().ToArray();
        if (recipients.Length > 0)
        {
            await notifications.SendAsync(new NotificationRequest(recipients, NotificationCategories.DocumentUpdate, ReleaseReturned, Link(doc.Id),
                DedupeKey: $"documents.release:{doc.ReleaseVersion:N}:returned:{doc.ReleaseInstance:N}"), ct);
        }
    }

    /// <summary>
    /// Puts a scheduled version in force: the version in force becomes superseded, the review date moves on, and
    /// <see cref="DocumentVersionPublished"/> starts the read confirmations.
    /// </summary>
    internal async Task<bool> MakeEffectiveAsync(Guid id, DateTimeOffset now, CancellationToken ct)
    {
        if (await files.RowAsync(id, forUpdate: true, ct) is not { Scheduled: { } versionId } doc)
        {
            return false;
        }
        var version = (await files.VersionAsync(versionId, ct))!;
        var settings = await libraries.ControlledOfAsync(doc.TypeId, ct);
        if (doc.Effective is { } previous)
        {
            await session.ExecAsync("update documents.document_version set state = 'superseded' where id = @previous", new { previous }, ct);
        }
        if (doc.ReviewInstance is { } review)
        {
            await engine.WithdrawAsync(review, ct);
        }
        var from = (await session.QuerySingleOrDefaultAsync("select effective_from from documents.document_version where id = @versionId", new { versionId },
            r => r.Get<DateTimeOffset?>("effective_from"), ct))!.Value;
        await session.ExecAsync("update documents.document_version set state = 'effective' where id = @versionId", new { versionId }, ct);
        await session.ExecAsync(
            """
            update documents.document set effective_version_id = @versionId, scheduled_version_id = null, withdrawn_at = null, review_due_at = @review,
              review_instance_id = null, review_overdue_notified_at = null, updated_at = @now, row_version = row_version + 1
            where id = @id
            """, new { id, versionId, review = from.AddMonths(settings?.Months ?? ControlledSettings.DefaultReviewMonths), now }, ct);
        var library = await libraries.Access.LibraryAsync(doc.LibraryId, ct);
        await audit.WriteAsync(new AuditEntry(AuditActions.DocVersionEffective, Securables.Document, id.ToString(), TargetOrgUnitId: library?.OrgUnitId,
            Changes: new { version = versionId, label = version.Label, superseded = doc.Effective }), ct);
        await outbox.EnqueueAsync(new DocumentVersionPublished(id, doc.LibraryId, versionId, version.Major, version.Minor), ct);
        return true;
    }

    // ---- Owner review (US3 scenario 5) ----

    /// <summary>The owner acts on their review task from the document; the inbox maps approve, return and reject the same way.</summary>
    public async Task<DocumentView> ReviewAsync(Guid id, DocumentReviewRequest request, CancellationToken ct)
    {
        var user = context.RequireUser();
        var action = request.Action ?? string.Empty;
        if (!DocumentReviewActions.All.Contains(action))
        {
            throw AppException.Validation("action", "validation.documents.review_action");
        }
        var (library, doc, _, settings) = await ControlledAsync(id, forUpdate: true, ct);
        if (doc.ReviewInstance is not { } instance)
        {
            throw AppException.Conflict("conflict.documents.no_review");
        }
        var task = (await engine.InstanceTasksAsync(instance, ct)).FirstOrDefault(t => t.Status == "open") ?? throw AppException.Conflict("conflict.documents.no_review");
        if (doc.Owner != user || await engine.MyTaskAsync(task.Id, ct) is null)
        {
            throw AppException.Denied();
        }
        DateTimeOffset? next = null;
        if (request.NextReviewOn is { } day)
        {
            next = await StartOfAsync(day, ct);
            if (next <= clock.UtcNow)
            {
                throw AppException.Validation("nextReviewOn", "validation.documents.review_date");
            }
        }
        await engine.CompleteTaskAsync(task.Id, action switch
        {
            DocumentReviewActions.Confirm => TaskOutcomes.Approve,
            DocumentReviewActions.Revise => TaskOutcomes.Return,
            _ => TaskOutcomes.Reject,
        }, null, ct);
        await ApplyReviewAsync(library, doc, settings, action, next, user, ct);
        return await files.GetAsync(id, ct);
    }

    internal async Task ApplyReviewAsync(LibraryRow library, DocumentFiles.DocRow doc, ControlledSettings settings, string action, DateTimeOffset? next, Guid actor,
        CancellationToken ct)
    {
        var now = clock.UtcNow;
        if (action == DocumentReviewActions.Withdraw)
        {
            // No longer in force: readers stop getting it, contributors and auditors keep the history.
            await session.ExecAsync("update documents.document_version set state = 'superseded' where id = @effective", new { effective = doc.Effective }, ct);
            await session.ExecAsync(
                """
                update documents.document set effective_version_id = null, withdrawn_at = @now, review_due_at = null, review_instance_id = null,
                  review_overdue_notified_at = null, updated_at = @now, row_version = row_version + 1
                where id = @id
                """, new { id = doc.Id, now }, ct);
            await outbox.EnqueueAsync(new DocumentWithdrawn(doc.Id, doc.LibraryId), ct);
        }
        else
        {
            await session.ExecAsync(
                """
                update documents.document set review_due_at = @due, review_instance_id = null, review_overdue_notified_at = null, updated_at = @now,
                  checked_out_by = case when @revise and checked_out_by is null then owner_user_id else checked_out_by end,
                  checked_out_at = case when @revise and checked_out_by is null then @now else checked_out_at end,
                  row_version = row_version + 1
                where id = @id
                """, new { id = doc.Id, due = next ?? now.AddMonths(settings.Months), revise = action == DocumentReviewActions.Revise, now }, ct);
        }
        await audit.WriteAsync(new AuditEntry(AuditActions.DocReviewed, Securables.Document, doc.Id.ToString(), TargetOrgUnitId: library.OrgUnitId,
            Changes: new { action, next, actor }), ct);
    }

    private async Task OpenReviewAsync(DocumentFiles.DocRow doc, CancellationToken ct)
    {
        var key = $"{ReviewPrefix}{doc.Owner:N}";
        if (await engine.GetDefinitionAsync(key, null, ct) is null)
        {
            await engine.PublishRouteAsync(key, RouteSources.DocumentRoute,
                new RouteSpec("document review", [new RouteStep("review", "review", new CandidateRule(Users: [doc.Owner]))], ExcludeStarter: false), ct);
        }
        var library = await libraries.Access.LibraryAsync(doc.LibraryId, ct);
        var instance = await engine.StartAsync(key, $"{doc.Id:N}{Ids.New():N}", library?.OrgUnitId, null, null, doc.Owner, ct);
        await session.ExecAsync("update documents.document set review_instance_id = @instance where id = @id", new { id = doc.Id, instance = instance.Id }, ct);
        await notifications.SendAsync(new NotificationRequest([doc.Owner], NotificationCategories.ApprovalWaiting, ReviewWaiting, Link(doc.Id),
            DedupeKey: $"documents.review:{instance.Id:N}"), ct);
    }

    // ---- Background work for one tenant ----

    /// <summary>Scheduled versions whose date came, review tasks whose window opened, and reviews now overdue.</summary>
    public async Task<int> RunDueAsync(DateTimeOffset now, int limit, CancellationToken ct)
    {
        var moved = 0;
        foreach (var id in await session.QueryAsync(
            """
            select d.id from documents.document d join documents.document_version v on v.id = d.scheduled_version_id
            where d.deleted_at is null and v.effective_from <= @now order by v.effective_from, d.id limit @limit
            """, new { now, limit }, r => r.GetGuid(0), ct))
        {
            moved += await MakeEffectiveAsync(id, now, ct) ? 1 : 0;
        }
        foreach (var id in await session.QueryAsync(
            """
            select id from documents.document
            where deleted_at is null and effective_version_id is not null and review_due_at <= @start and review_instance_id is null and release_instance_id is null
            order by review_due_at, id limit @limit
            """, new { start = now + ReviewWindow, limit }, r => r.GetGuid(0), ct))
        {
            if (await files.RowAsync(id, forUpdate: true, ct) is { ReviewInstance: null, ReviewDueAt: not null, Effective: not null } doc)
            {
                await OpenReviewAsync(doc, ct);
                moved++;
            }
        }
        var overdue = await session.QueryAsync(
            """
            select id, owner_user_id, review_due_at from documents.document
            where deleted_at is null and effective_version_id is not null and review_due_at <= @now and review_overdue_notified_at is null
            order by review_due_at, id limit @limit
            """, new { now, limit }, r => (Id: r.GetGuid(0), Owner: r.GetGuid(1), Due: r.GetFieldValue<DateTimeOffset>(2)), ct);
        if (overdue.Count > 0)
        {
            var managers = await acl.TenantHoldersAsync(Permissions.DocManage, ct);
            foreach (var (id, owner, due) in overdue)
            {
                await notifications.SendAsync(new NotificationRequest(managers.Append(owner).Distinct().ToArray(), NotificationCategories.DocumentUpdate, ReviewOverdue, Link(id),
                    DedupeKey: $"documents.review:{id:N}:overdue:{due:yyyyMMdd}"), ct);
                await session.ExecAsync("update documents.document set review_overdue_notified_at = @now where id = @id", new { id, now }, ct);
                moved++;
            }
        }
        return moved;
    }

    // ---- Reading ----

    /// <summary>One version with the superseded banner's details (US3 scenario 3). Readers see only versions that were in force.</summary>
    public async Task<VersionDetailView> VersionAsync(Guid id, Guid versionId, CancellationToken ct)
    {
        var (_, doc, rights) = await files.VisibleAsync(id, ct);
        var version = await session.ScalarAsync<Guid?>("select id from documents.document_version where id = @versionId and document_id = @id and state <> 'pending'",
            new { versionId, id }, ct) is { } found ? await files.VersionAsync(found, ct) : null;
        if (version is null || (!rights.Contribute && version.State is not (VersionStates.Effective or VersionStates.Superseded)))
        {
            throw AppException.NotFound();
        }
        return new VersionDetailView(version, version.State == VersionStates.Superseded, await files.VersionAsync(doc.Effective, ct), doc.Number);
    }

    /// <summary>Documents of a library with a review date, the soonest first, for its managers.</summary>
    public async Task<IReadOnlyList<ReviewItem>> ReviewsAsync(Guid libraryId, CancellationToken ct)
    {
        var (library, _) = await libraries.VisibleAsync(libraryId, ct);
        var rights = await libraries.Access.RightsAsync(library, await libraries.Access.SecurablesAsync(library, ct), ct);
        if (!rights[library.Id].Manage)
        {
            throw AppException.Denied();
        }
        var now = clock.UtcNow;
        return await session.QueryAsync(
            """
            select id, name, number, owner_user_id, review_due_at, review_instance_id is not null from documents.document
            where library_id = @libraryId and deleted_at is null and review_due_at is not null order by review_due_at, id limit 500
            """, new { libraryId },
            r => new ReviewItem(r.GetGuid(0), r.GetString(1), r.Get<string>("number"), r.GetGuid(3), r.GetFieldValue<DateTimeOffset>(4), r.GetFieldValue<DateTimeOffset>(4) <= now,
                r.GetBoolean(5)), ct);
    }
}

/// <summary>
/// Controlled document releases and owner reviews in the spec 016 task inbox. Named approvers hear that a release
/// waits; the decision moves the document on in the same transaction.
/// </summary>
public sealed class DocumentTaskSource(
    ITenantContext context,
    IProcessEngine engine,
    DocumentFiles files,
    Libraries libraries,
    DocumentReleases releases,
    INotificationSender notifications) : ITaskSource, IMessageHandler<UserTaskCreated>
{
    public bool Handles(string definitionKey) => DocumentReleases.IsRelease(definitionKey) || DocumentReleases.IsReview(definitionKey);

    private async Task<(LibraryRow Library, DocumentFiles.DocRow Doc)?> WaitingAsync(UserTaskInfo task, bool forUpdate, CancellationToken ct)
    {
        if (DocumentReleases.DocumentIdOf(task.BusinessKey) is not { } id || await files.RowAsync(id, forUpdate, ct) is not { } doc
            || (DocumentReleases.IsRelease(task.DefinitionKey) ? doc.ReleaseInstance : doc.ReviewInstance) != task.InstanceId
            || await libraries.Access.LibraryAsync(doc.LibraryId, ct) is not { } library)
        {
            return null;
        }
        return (library, doc);
    }

    public async Task<TaskSubject?> DescribeAsync(UserTaskInfo task, CancellationToken ct = default)
    {
        if (await WaitingAsync(task, false, ct) is not var (library, doc))
        {
            return null;
        }
        // The file name is the title in every language; the document's text stays behind its read check.
        var title = library.Names.Keys.ToDictionary(k => k, _ => doc.Number is { } n ? $"{n} {doc.Name}" : doc.Name);
        var release = DocumentReleases.IsRelease(task.DefinitionKey);
        return new TaskSubject(release ? "document_release" : "document_review", title, DocumentReleases.Link(doc.Id), library.OrgUnitId, release ? null : doc.Owner);
    }

    /// <summary>The engine already keeps the author and submitter out; this holds the line for the person whose authority is used.</summary>
    public async Task<bool> MayDecideAsync(UserTaskInfo task, Guid authority, CancellationToken ct = default)
    {
        if (await WaitingAsync(task, false, ct) is not var (_, doc))
        {
            return false;
        }
        if (DocumentReleases.IsReview(task.DefinitionKey))
        {
            return authority == doc.Owner;
        }
        var author = (await files.VersionAsync(doc.ReleaseVersion, ct))?.AuthorUserId;
        return authority != author && authority != await SubmitterAsync(doc.Id, ct);
    }

    private Task<Guid?> SubmitterAsync(Guid id, CancellationToken ct) => files.ReleaseSubmitterAsync(id, ct);

    public async Task DecidedAsync(UserTaskInfo task, UserTaskInfo done, CancellationToken ct = default)
    {
        if (await WaitingAsync(task, true, ct) is not var (library, doc))
        {
            return;
        }
        var decider = done.CompletedBy ?? context.RequireUser();
        if (DocumentReleases.IsReview(task.DefinitionKey))
        {
            var settings = await libraries.ControlledOfAsync(doc.TypeId, ct);
            if (settings is null)
            {
                return;
            }
            await releases.ApplyReviewAsync(library, doc, settings, done.Outcome switch
            {
                TaskOutcomes.Approve => DocumentReviewActions.Confirm,
                TaskOutcomes.Return => DocumentReviewActions.Revise,
                _ => DocumentReviewActions.Withdraw,
            }, null, decider, ct);
            return;
        }
        var instance = await engine.GetInstanceAsync(task.InstanceId, ct);
        if (instance is not { Status: "completed" })
        {
            return; // The next step is open.
        }
        if (instance.Outcome == InstanceOutcomes.Approved)
        {
            await releases.ApprovedAsync(library, doc, decider, done.OnBehalfOf, ct);
        }
        else
        {
            await releases.ReturnedAsync(library, doc, instance.Outcome, decider, ct);
        }
    }

    /// <summary>Named approvers of the step that just opened hear that a release waits for them.</summary>
    public async Task HandleAsync(UserTaskCreated message, CancellationToken ct)
    {
        if (!DocumentReleases.IsRelease(message.DefinitionKey) || DocumentReleases.DocumentIdOf(message.BusinessKey) is not { } id
            || await files.RowAsync(id, false, ct) is not { } doc || doc.ReleaseInstance != message.InstanceId
            || await libraries.ControlledOfAsync(doc.TypeId, ct) is not { Steps: { } steps }
            || !message.ElementId.StartsWith('s') || !int.TryParse(message.ElementId[1..], out var index) || index >= steps.Count)
        {
            return;
        }
        var author = (await files.VersionAsync(doc.ReleaseVersion, ct))?.AuthorUserId;
        var submitter = await SubmitterAsync(id, ct);
        var named = (steps[index].Approvers ?? []).Where(a => a != author && a != submitter).ToArray();
        if (named.Length > 0)
        {
            await notifications.SendAsync(new NotificationRequest(named, NotificationCategories.ApprovalWaiting, DocumentReleases.ReleaseWaiting,
                DocumentReleases.Link(id) + "?task=" + message.TaskId, DedupeKey: "task:" + message.TaskId.ToString("N")), ct);
        }
    }
}

/// <summary>
/// Read confirmation (spec 011 FR-008, US3 scenario 4): when a major version of a type that asks for it comes into
/// force, everyone who may read the document is asked to confirm, and each confirmation is kept per version. Spec 009
/// keeps its acknowledgements on content items, so documents keep theirs with the same fields (person, version, time).
/// </summary>
public sealed class DocumentReadConfirmations(
    IDbSession session,
    ITenantContext context,
    DocumentFiles files,
    Libraries libraries,
    ITenantMemberships memberships,
    IAuthorizationProvisioning acl,
    INotificationSender notifications,
    IAuditWriter audit,
    IOutbox outbox,
    IClock clock) : IMessageHandler<DocumentVersionPublished>
{
    public const string Requested = "documents.read_confirmation";
    public const int NoticeBatch = 500;

    public async Task HandleAsync(DocumentVersionPublished message, CancellationToken ct)
    {
        if (message.Minor != 0 || await files.RowAsync(message.DocumentId, false, ct) is not { } doc || doc.Effective != message.VersionId
            || await libraries.ControlledOfAsync(doc.TypeId, ct) is not { ReadConfirmation: true }
            || await libraries.Access.LibraryAsync(doc.LibraryId, ct) is not { } library)
        {
            return;
        }
        var target = AuthzTarget.Resource(DocumentAccess.Describe(library, new Securable(doc.SecurableType, doc.Securable, doc.Classification)));
        var audience = new List<Guid>();
        foreach (var member in await memberships.ListAsync(ct))
        {
            if (member.Status == MembershipStatus.Active && await acl.UserHoldsAsync(member.UserId, Permissions.DocView, target, ct))
            {
                audience.Add(member.UserId);
            }
        }
        if (audience.Count == 0)
        {
            return;
        }
        await session.ExecAsync(
            """
            insert into documents.read_confirmation (tenant_id, document_id, version_id, user_id, requested_at)
            select platform.current_tenant_id(), @doc, @version, u, @now from unnest(@users) u
            on conflict do nothing
            """, new { doc = doc.Id, version = message.VersionId, users = audience.ToArray(), now = clock.UtcNow }, ct);
        for (var i = 0; i < audience.Count; i += NoticeBatch)
        {
            await notifications.SendAsync(new NotificationRequest(audience.Skip(i).Take(NoticeBatch).ToArray(), NotificationCategories.AttestationDue, Requested,
                DocumentReleases.Link(doc.Id), DedupeKey: $"documents.read:{message.VersionId:N}:{i / NoticeBatch}"), ct);
        }
    }

    /// <summary>The caller confirms they have read the version in force.</summary>
    public async Task<DocumentView> ConfirmAsync(Guid id, CancellationToken ct)
    {
        var user = context.RequireUser();
        var (library, doc, _) = await files.VisibleAsync(id, ct);
        if (doc.Effective is not { } version)
        {
            throw AppException.Conflict("conflict.documents.not_effective");
        }
        if (await libraries.ControlledOfAsync(doc.TypeId, ct) is not { ReadConfirmation: true })
        {
            throw AppException.Conflict("conflict.documents.no_confirmation");
        }
        var first = await session.ScalarAsync<bool>(
            """
            with up as (
              insert into documents.read_confirmation (tenant_id, document_id, version_id, user_id, requested_at, confirmed_at)
              values (platform.current_tenant_id(), @id, @version, @user, @now, @now)
              on conflict (tenant_id, version_id, user_id) do update set confirmed_at = coalesce(documents.read_confirmation.confirmed_at, excluded.confirmed_at)
              returning confirmed_at = @now as fresh
            ) select fresh from up
            """, new { id, version, user, now = clock.UtcNow }, ct);
        if (first)
        {
            await audit.WriteAsync(new AuditEntry(AuditActions.DocReadConfirmed, Securables.Document, id.ToString(), TargetOrgUnitId: library.OrgUnitId,
                Changes: new { version }), ct);
            await outbox.EnqueueAsync(new DocumentReadConfirmed(id, version, user), ct);
        }
        return await files.GetAsync(id, ct);
    }

    /// <summary>Who was asked and who confirmed, for the library's managers.</summary>
    public async Task<ReadConfirmationReport> ReportAsync(Guid id, CancellationToken ct)
    {
        var (_, doc, rights) = await files.VisibleAsync(id, ct);
        if (!rights.Manage)
        {
            throw AppException.Denied();
        }
        if (doc.Effective is not { } version)
        {
            throw AppException.Conflict("conflict.documents.not_effective");
        }
        var label = (await files.VersionAsync(version, ct))!.Label;
        var counts = await session.QuerySingleOrDefaultAsync(
            "select count(*), count(confirmed_at) from documents.read_confirmation where version_id = @version", new { version },
            r => (Requested: (int)r.GetInt64(0), Confirmed: (int)r.GetInt64(1)), ct);
        var pending = await session.QueryAsync(
            "select user_id from documents.read_confirmation where version_id = @version and confirmed_at is null order by requested_at, user_id limit 500",
            new { version }, r => r.GetGuid(0), ct);
        return new ReadConfirmationReport(version, label, counts.Requested, counts.Confirmed, pending);
    }
}
