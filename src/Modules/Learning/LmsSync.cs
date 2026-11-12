using TeamBeats.Authorization.Contracts;
using TeamBeats.Entitlements.Contracts;
using TeamBeats.Messaging;
using TeamBeats.Modules.Content.Contracts;
using TeamBeats.Modules.Identity.Contracts;
using TeamBeats.Modules.Integrations.Contracts;
using TeamBeats.Modules.Search.Contracts;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Primitives;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.Learning;

/// <summary>Where the LMS sync stands, for learning administrators.</summary>
public sealed record LmsStatus(bool Configured, bool Available, DateTimeOffset? CatalogueAt, DateTimeOffset? CompletionsAt, int Courses, int Waiting, int Refused);

/// <summary>
/// Whether courses from the LMS can be opened now (US6 scenario 3): the package has the LMS connector, the tenant has an
/// enabled LMS connection, and the LMS answered the last time the sync called it. Read once per request.
/// </summary>
public sealed class LmsAvailability(IEntitlementService entitlements, ILmsConnector lms, IDbSession session)
{
    private bool? available;

    public async Task<bool> AvailableAsync(CancellationToken ct) =>
        available ??= await entitlements.IsEnabledAsync(Capabilities.LearningLmsConnector, ct) && await lms.ConfiguredAsync(ct)
            && (await session.ScalarAsync<bool?>("select available from learning.lms_state", null, ct) ?? true);
}

/// <summary>
/// The LMS sync (spec 028 US6, FR-015). The catalogue is read every 6 hours: each LMS course becomes a published course
/// with no lessons that opens the LMS, and a changed title or summary is published as a new version. A course the LMS no
/// longer lists is left as it is, for an administrator to retire, so a short or partial answer never withdraws
/// assignments. Open assignments of LMS courses are pushed once, by the learner's work email. Completions are read every
/// 15 minutes with an hour of overlap; each completes the learner's open assignment at the time the LMS gives, or records
/// a completion when there is none, and issues the certificate as the player does. When the LMS does not answer, its
/// courses show as unavailable and everything else carries on.
/// </summary>
public sealed class LmsSync(
    IDbSession session,
    ITenantContext context,
    IAuthorizationService authz,
    IEntitlementService entitlements,
    ILmsConnector lms,
    IUserDirectory users,
    ITenantSettingsReader tenantSettings,
    Player player,
    IOutbox outbox,
    IClock clock)
{
    public static readonly TimeSpan CatalogueEvery = TimeSpan.FromHours(6);
    public static readonly TimeSpan CompletionsEvery = TimeSpan.FromMinutes(15);
    public static readonly TimeSpan Overlap = TimeSpan.FromHours(1);
    public static readonly TimeSpan FirstLookBack = TimeSpan.FromDays(30);
    public const int PushBatch = 200;
    public const string ScoreLesson = "lms";

    private sealed record State(bool Available, DateTimeOffset? CatalogueAt, DateTimeOffset? CompletionsAt, DateTimeOffset? CompletionsSince);

    private async Task<State> StateAsync(CancellationToken ct) =>
        await session.QuerySingleOrDefaultAsync("select available, catalogue_at, completions_at, completions_since from learning.lms_state", null,
            r => new State(r.GetBoolean(0), r.Get<DateTimeOffset?>("catalogue_at"), r.Get<DateTimeOffset?>("completions_at"), r.Get<DateTimeOffset?>("completions_since")), ct)
        ?? new State(true, null, null, null);

    private Task SaveAsync(bool available, DateTimeOffset now, DateTimeOffset? catalogueAt, DateTimeOffset? completionsAt, DateTimeOffset? since, CancellationToken ct) =>
        session.ExecAsync(
            """
            insert into learning.lms_state (tenant_id, available, catalogue_at, completions_at, completions_since, checked_at)
            values (@tenant, @available, @catalogueAt, @completionsAt, @since, @now)
            on conflict (tenant_id) do update set available = excluded.available, checked_at = excluded.checked_at,
              catalogue_at = coalesce(excluded.catalogue_at, learning.lms_state.catalogue_at),
              completions_at = coalesce(excluded.completions_at, learning.lms_state.completions_at),
              completions_since = coalesce(excluded.completions_since, learning.lms_state.completions_since)
            """, new { tenant = context.RequireTenant(), available, catalogueAt, completionsAt, since, now }, ct);

    private async Task DemandAsync(CancellationToken ct)
    {
        await entitlements.DemandAsync(Capabilities.LearningLmsConnector, ct);
        context.RequireUser();
        await authz.DemandAsync(Permissions.LearningManage, AuthzTarget.Tenant, ct);
    }

    public async Task<LmsStatus> StatusAsync(CancellationToken ct)
    {
        await DemandAsync(ct);
        var state = await StateAsync(ct);
        var (count, waiting, refused) = await session.QuerySingleOrDefaultAsync(
            """
            select (select count(*)::int from learning.course where external_id is not null),
              (select count(*)::int from learning.assignment a join learning.course c on c.id = a.course_id
               where c.external_id is not null and a.status = 'open' and a.lms_push is null),
              (select count(*)::int from learning.assignment a join learning.course c on c.id = a.course_id
               where c.external_id is not null and a.status = 'open' and a.lms_push = 'refused')
            """, null, r => (r.GetInt32(0), r.GetInt32(1), r.GetInt32(2)), ct);
        var configured = await lms.ConfiguredAsync(ct);
        return new LmsStatus(configured, configured && state.Available, state.CatalogueAt, state.CompletionsAt, count, waiting, refused);
    }

    /// <summary>Reads the catalogue and completions and pushes waiting assignments now, for a learning administrator.</summary>
    public async Task<LmsStatus> SyncNowAsync(CancellationToken ct)
    {
        await DemandAsync(ct);
        await RunAsync(clock.UtcNow, true, ct);
        return await StatusAsync(ct);
    }

    /// <summary>The maintenance worker's turn: whatever is due. Does nothing without the capability or a connection.</summary>
    public async Task RunDueAsync(DateTimeOffset now, CancellationToken ct)
    {
        if (await entitlements.IsEnabledAsync(Capabilities.LearningLmsConnector, ct) && await lms.ConfiguredAsync(ct))
        {
            await RunAsync(now, false, ct);
        }
    }

    private async Task RunAsync(DateTimeOffset now, bool all, CancellationToken ct)
    {
        var state = await StateAsync(ct);
        var available = state.Available;
        if (all || state.CatalogueAt is not { } c || now - c >= CatalogueEvery)
        {
            available = await ImportAsync(now, ct);
        }
        if (available && (all || state.CompletionsAt is not { } d || now - d >= CompletionsEvery))
        {
            available = await PullAsync(now, state.CompletionsSince ?? now - FirstLookBack, ct);
        }
        if (available)
        {
            available = await PushAsync(ct);
        }
        if (!available)
        {
            await SaveAsync(false, now, null, null, null, ct);
        }
    }

    // ---- Catalogue ----

    private async Task<bool> ImportAsync(DateTimeOffset now, CancellationToken ct)
    {
        var read = await lms.CatalogueAsync(ct);
        if (read.Result != LmsResults.Ok)
        {
            return false;
        }
        var locales = await tenantSettings.GetLocaleSettingsAsync(ct);
        foreach (var item in read.Items.GroupBy(i => i.ExternalId, StringComparer.Ordinal).Select(g => g.First()))
        {
            var locale = item.Language is { } l && locales.EnabledLocales.Contains(l) ? l
                : item.Language is { Length: > 2 } full && locales.EnabledLocales.Contains(full[..2]) ? full[..2] : locales.DefaultLocale;
            var definition = new CourseDefinition(
                new Dictionary<string, string> { [locale] = item.Title },
                item.Summary is { Length: > 0 } summary ? new Dictionary<string, string> { [locale] = summary } : new Dictionary<string, string>(),
                null, []);
            await UpsertAsync(item, definition, now, ct);
        }
        await SaveAsync(true, now, now, null, null, ct);
        return true;
    }

    private async Task UpsertAsync(LmsCourse item, CourseDefinition definition, DateTimeOffset now, CancellationToken ct)
    {
        var tenant = context.RequireTenant();
        var actor = context.ActorId ?? Guid.Empty;
        var existing = await session.QuerySingleOrDefaultAsync($"select {Courses.Columns} from learning.course where external_id = @id for update", new { id = item.ExternalId },
            Courses.Read, ct);
        if (existing is null)
        {
            var id = Ids.New();
            await session.ExecAsync(
                """
                insert into learning.course (tenant_id, id, status, draft, draft_revision, published_revision, published_version, audience, open_enrolment, created_by,
                  created_at, updated_at, external_id, launch_url)
                values (@tenant, @id, 'published', @draft, 1, 1, 1, @audience, false, @actor, @now, @now, @externalId, @launch)
                """, new
                {
                    tenant, id, draft = LearningJson.Jsonb(definition), audience = LearningJson.Jsonb(ContentAudience.Everyone), actor, now, externalId = item.ExternalId,
                    launch = item.LaunchUrl,
                }, ct);
            await VersionAsync(id, 1, definition, actor, now, ct);
            await outbox.EnqueueAsync(new SearchItemChanged(LearningSearchSource.Name, id), ct);
            return;
        }
        var same = Text(existing.DraftDefinition) == Text(definition);
        if (same && existing.LaunchUrl == item.LaunchUrl)
        {
            return;
        }
        if (same)
        {
            await session.ExecAsync("update learning.course set launch_url = @launch, updated_at = @now where id = @id", new { id = existing.Id, launch = item.LaunchUrl, now }, ct);
            return;
        }
        // A retired course stays retired: retiring is the administrator's decision.
        var version = (existing.PublishedVersion ?? 0) + 1;
        await VersionAsync(existing.Id, version, definition, actor, now, ct);
        await session.ExecAsync(
            """
            update learning.course set draft = @draft, draft_revision = draft_revision + 1, published_revision = draft_revision + 1, published_version = @version,
              launch_url = @launch, updated_at = @now
            where id = @id
            """, new { id = existing.Id, draft = LearningJson.Jsonb(definition), version, launch = item.LaunchUrl, now }, ct);
        await outbox.EnqueueAsync(new SearchItemChanged(LearningSearchSource.Name, existing.Id), ct);
    }

    private static string Text(CourseDefinition d) => System.Text.Json.JsonSerializer.Serialize(d, LearningJson.Options);

    private Task VersionAsync(Guid id, int version, CourseDefinition definition, Guid actor, DateTimeOffset now, CancellationToken ct) =>
        session.ExecAsync(
            """
            insert into learning.course_version (tenant_id, course_id, version, definition, sources, requires_retraining, published_by, published_at)
            values (@tenant, @id, @version, @definition, '{}', false, @actor, @now)
            """, new { tenant = context.RequireTenant(), id, version, definition = LearningJson.Jsonb(definition), actor, now }, ct);

    // ---- Assignments ----

    private async Task<bool> PushAsync(CancellationToken ct)
    {
        var waiting = await session.QueryAsync(
            $"""
            select a.id, c.external_id, a.learner_id, a.due_on from learning.assignment a join learning.course c on c.id = a.course_id
            where a.status = 'open' and a.lms_push is null and c.external_id is not null
            order by a.created_at, a.id limit {PushBatch}
            """, null, r => (Id: r.GetGuid(0), Course: r.GetString(1), Learner: r.GetGuid(2), Due: r.Get<DateOnly?>("due_on")), ct);
        if (waiting.Count == 0)
        {
            return true;
        }
        var emails = await users.GetManyAsync(waiting.Select(w => w.Learner).Distinct().ToList(), ct);
        foreach (var w in waiting)
        {
            if (!emails.TryGetValue(w.Learner, out var user) || string.IsNullOrEmpty(user.Email))
            {
                await session.ExecAsync("update learning.assignment set lms_push = 'refused' where id = @id", new { id = w.Id }, ct);
                continue;
            }
            var result = await lms.AssignAsync(new LmsAssignment(w.Id, w.Course, user.Email, w.Due), ct);
            if (result is LmsResults.Unavailable or LmsResults.NotConfigured)
            {
                return result == LmsResults.NotConfigured;
            }
            await session.ExecAsync("update learning.assignment set lms_push = @push where id = @id",
                new { id = w.Id, push = result == LmsResults.Ok ? "queued" : "refused" }, ct);
        }
        return true;
    }

    // ---- Completions ----

    private async Task<bool> PullAsync(DateTimeOffset now, DateTimeOffset since, CancellationToken ct)
    {
        var read = await lms.CompletionsAsync(since, ct);
        if (read.Result != LmsResults.Ok)
        {
            return false;
        }
        var people = new Dictionary<string, Guid?>(StringComparer.OrdinalIgnoreCase);
        var rows = new Dictionary<string, Courses.CourseRow?>(StringComparer.Ordinal);
        foreach (var completion in read.Items.Where(c => c.CompletedAt <= now).OrderBy(c => c.CompletedAt))
        {
            if (!rows.TryGetValue(completion.ExternalCourseId, out var row))
            {
                row = await session.QuerySingleOrDefaultAsync($"select {Courses.Columns} from learning.course where external_id = @id and status = 'published'",
                    new { id = completion.ExternalCourseId }, Courses.Read, ct);
                rows[completion.ExternalCourseId] = row;
            }
            if (!people.TryGetValue(completion.LearnerEmail, out var learner))
            {
                learner = await users.FindByEmailAsync(completion.LearnerEmail, ct) is { Status: "active" } u ? u.Id : null;
                people[completion.LearnerEmail] = learner;
            }
            if (row is not null && learner is { } person)
            {
                await CompleteAsync(row, person, completion, ct);
            }
        }
        await SaveAsync(true, now, null, now, now - Overlap, ct);
        return true;
    }

    private async Task CompleteAsync(Courses.CourseRow row, Guid learner, LmsCompletion completion, CancellationToken ct)
    {
        var assignment = await player.CurrentAssignmentAsync(row.Id, learner, true, ct);
        if (assignment is { Status: AssignmentStatuses.Completed, CompletedAt: { } done } && done >= completion.CompletedAt.AddSeconds(-1))
        {
            // Already recorded: the overlap reads it again.
            return;
        }
        if (assignment is not { Status: AssignmentStatuses.Open })
        {
            assignment = await player.AssignAsync(row.Id, learner, AssignmentSources.Self, null, ct);
            // It is complete as it is created, so there is nothing to push.
            await session.ExecAsync("update learning.assignment set lms_push = 'queued' where id = @id", new { id = assignment.Id }, ct);
        }
        await session.ExecAsync("update learning.assignment set started_at = coalesce(started_at, @at) where id = @id", new { id = assignment.Id, at = completion.CompletedAt }, ct);
        if (completion.Score is { } score)
        {
            await session.ExecAsync(
                """
                insert into learning.attempt (tenant_id, id, assignment_id, lesson_key, course_version, score, passed, created_at)
                values (@tenant, @id, @assignment, @lesson, @version, @score, true, @at)
                """, new { tenant = context.RequireTenant(), id = Ids.New(), assignment = assignment.Id, lesson = ScoreLesson, version = row.PublishedVersion, score, at = completion.CompletedAt },
                ct);
        }
        await player.FinishAsync(row, assignment, completion.CompletedAt, ct);
    }
}
