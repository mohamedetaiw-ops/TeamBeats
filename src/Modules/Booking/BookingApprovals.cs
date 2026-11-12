using System.Globalization;
using TeamBeats.Authorization.Contracts;
using TeamBeats.Localization;
using TeamBeats.Messaging;
using TeamBeats.Modules.Booking.Contracts;
using TeamBeats.Modules.Notifications.Contracts;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Modules.Workflow.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Primitives;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.Booking;

/// <summary>
/// Restricted resources (spec 031 US3, FR-007). A booking of a resource that needs approval is held as tentative and
/// its approval runs on the spec 024 engine, so it shows in the spec 016 task inbox. The resource's named approvers and
/// the facilities administrators of its site decide; the person booked for and the person who booked never do. On
/// approval the booking is confirmed; on rejection the slot frees and the holder sees the reason; a hold nobody decided
/// in time expires (see <see cref="BookingMaintenance"/>). A series waits on one approval for all its occurrences.
/// </summary>
public sealed class BookingApprovals(
    IDbSession session,
    ITenantContext context,
    IProcessEngine engine,
    ITenantSettingsReader tenantSettings,
    INotificationSender notifications,
    ILocalizer localizer,
    IOutbox outbox,
    IClock clock) : ITaskSource, IMessageHandler<UserTaskCreated>
{
    public const string KeyPrefix = "booking.approval:";
    public const string RequestedTemplate = "booking.requested";
    public const string WaitingTemplate = "booking.approval_waiting";
    public const string ApprovedTemplate = "booking.approved";
    public const string RejectedTemplate = "booking.rejected";
    public const string ExpiredTemplate = "booking.expired";
    public const int MaxNoteLength = 2000;

    /// <summary>One route per resource, so its own approvers decide; republishing the same route keeps its version.</summary>
    public static string ProcessKey(Guid resourceId) => KeyPrefix + resourceId.ToString("N");

    internal sealed record Held(Guid Id, Guid ResourceId, Guid HolderId, Guid BookedBy, DateTimeOffset StartsAt, DateTimeOffset EndsAt, string Kind, Guid SiteId, Guid? OrgUnitId,
        Dictionary<string, string> ResourceNames, string TimeZone, Guid[] ApproverIds);

    private const string HeldSelect =
        """
        select b.id, b.resource_id, b.holder_id, b.booked_by, b.starts_at, b.ends_at, r.kind, r.site_id, s.org_unit_id, r.names::text, s.time_zone, r.approver_ids
        from booking.booking b
        join booking.resource r on r.id = b.resource_id
        join booking.site s on s.id = r.site_id
        """;

    private static Held ReadHeld(Npgsql.NpgsqlDataReader r) => new(r.GetGuid(0), r.GetGuid(1), r.GetGuid(2), r.GetGuid(3), r.GetFieldValue<DateTimeOffset>(4),
        r.GetFieldValue<DateTimeOffset>(5), r.GetString(6), r.GetGuid(7), r.Get<Guid?>("org_unit_id"), Names.Read(r, 9), r.GetString(10), r.GetFieldValue<Guid[]>(11));

    /// <summary>The bookings still waiting on an approval instance, first occurrence first.</summary>
    internal Task<List<Held>> WaitingAsync(Guid instanceId, bool forUpdate, CancellationToken ct) =>
        session.QueryAsync($"{HeldSelect} where b.instance_id = @instanceId and b.status = 'tentative' order by b.starts_at{(forUpdate ? " for update of b" : "")}",
            new { instanceId }, ReadHeld, ct);

    /// <summary>Starts the approval of a booking request; the business key is its first booking.</summary>
    internal async Task<Guid> StartAsync(ResourceRow resource, SiteRow site, Guid firstBooking, Guid holder, Guid bookedBy, CancellationToken ct)
    {
        var key = ProcessKey(resource.Id);
        await engine.PublishRouteAsync(key, RouteSources.BookingRoute, new RouteSpec("booking approval",
            [new RouteStep("decision", "Booking decision", new CandidateRule(Users: resource.ApproverIds, Permission: Permissions.BookingManage))]), ct);
        var instance = await engine.StartAsync(key, firstBooking.ToString("N"), site.OrgUnitId, null, [holder, bookedBy], holder, ct: ct);
        return instance.Id;
    }

    /// <summary>Ends the approval of an instance once none of its bookings waits any more (cancelled, removed, or the holder left).</summary>
    internal async Task EndIfIdleAsync(Guid instanceId, CancellationToken ct)
    {
        if (await session.ScalarAsync<bool>("select exists (select 1 from booking.booking where instance_id = @instanceId and status = 'tentative')", new { instanceId }, ct))
        {
            return;
        }
        if (await engine.GetInstanceAsync(instanceId, ct) is { Status: "active" })
        {
            await engine.WithdrawAsync(instanceId, ct);
        }
    }

    private async Task<Dictionary<string, string>> DescribeAsync(Held first, int count, CancellationToken ct)
    {
        var locale = (await tenantSettings.GetLocaleSettingsAsync(ct)).DefaultLocale;
        var parameters = BookingCancellations.Describe(first.ResourceNames, first.TimeZone, first.StartsAt, first.EndsAt, locale);
        parameters["count"] = count.ToString(CultureInfo.InvariantCulture);
        return parameters;
    }

    /// <summary>The approval decided: approved bookings that have not ended are confirmed, a rejection frees the slot with the reason.</summary>
    internal async Task DecidedAsync(Guid instanceId, string outcome, Guid decider, string? comment, CancellationToken ct)
    {
        var now = clock.UtcNow;
        var approved = outcome == InstanceOutcomes.Approved;
        var note = string.IsNullOrWhiteSpace(comment) ? null : comment.Trim()[..Math.Min(comment.Trim().Length, MaxNoteLength)];
        var ids = await session.QueryAsync(
            approved
                ? "update booking.booking set status = 'confirmed', hold_until = null, decided_by = @decider, row_version = row_version + 1 where instance_id = @instanceId and status = 'tentative' and ends_at > @now returning id"
                : "update booking.booking set status = 'rejected', hold_until = null, decided_by = @decider, decision_note = @note, ended_at = @now, end_reason = 'rejected', row_version = row_version + 1 where instance_id = @instanceId and status = 'tentative' returning id",
            new { instanceId, decider, note, now }, r => r.GetGuid(0), ct);
        if (ids.Count == 0)
        {
            return;
        }
        var decided = await session.QueryAsync($"{HeldSelect} where b.id = any(@ids) order by b.starts_at", new { ids = ids.ToArray() }, ReadHeld, ct);
        var first = decided[0];
        if (approved)
        {
            foreach (var b in decided)
            {
                await outbox.EnqueueAsync(new BookingConfirmed(b.Id, b.ResourceId, b.SiteId, b.Kind, b.HolderId, b.StartsAt, b.EndsAt), ct);
            }
        }
        var parameters = await DescribeAsync(first, decided.Count, ct);
        if (!approved)
        {
            parameters["reason"] = note ?? "";
        }
        await notifications.SendAsync(new NotificationRequest([first.HolderId], NotificationCategories.Bookings, approved ? ApprovedTemplate : RejectedTemplate,
            Bookings.Link(first.Id), parameters, DedupeKey: $"booking:{first.Id:N}:decided"), ct);
    }

    /// <summary>Tentative holds whose time ran out (US3 scenario 1): they expire, the approval ends and the holder is told. Returns how many expired.</summary>
    internal async Task<int> ExpireDueAsync(DateTimeOffset now, CancellationToken ct)
    {
        var expired = await session.QueryAsync(
            """
            update booking.booking set status = 'expired', ended_at = @now, end_reason = 'expired', row_version = row_version + 1
            where status = 'tentative' and hold_until <= @now
            returning id, instance_id
            """, new { now }, r => (Id: r.GetGuid(0), Instance: r.Get<Guid?>("instance_id")), ct);
        foreach (var group in expired.GroupBy(e => e.Instance))
        {
            var ids = group.Select(e => e.Id).ToArray();
            var rows = await session.QueryAsync($"{HeldSelect} where b.id = any(@ids) order by b.starts_at", new { ids }, ReadHeld, ct);
            if (group.Key is { } instance)
            {
                await EndIfIdleAsync(instance, ct);
            }
            await notifications.SendAsync(new NotificationRequest([rows[0].HolderId], NotificationCategories.Bookings, ExpiredTemplate, Bookings.Link(rows[0].Id),
                await DescribeAsync(rows[0], rows.Count, ct), DedupeKey: $"booking:{rows[0].Id:N}:expired"), ct);
        }
        return expired.Count;
    }

    // ---- The spec 016 task inbox ----

    public bool Handles(string definitionKey) => definitionKey.StartsWith(KeyPrefix, StringComparison.Ordinal);

    async Task<TaskSubject?> ITaskSource.DescribeAsync(UserTaskInfo task, CancellationToken ct)
    {
        var waiting = await WaitingAsync(task.InstanceId, false, ct);
        if (waiting.Count == 0)
        {
            return null;
        }
        var first = waiting[0];
        var parameters = await DescribeAsync(first, waiting.Count, ct);
        var title = Locales.LaunchComplete.ToDictionary(l => l, l =>
        {
            var text = localizer.Get("booking.task.approval", l);
            foreach (var (k, v) in parameters)
            {
                text = text.Replace("{" + k + "}", k == "resource" ? Names.Pick(first.ResourceNames, l) : v, StringComparison.Ordinal);
            }
            return text;
        });
        return new TaskSubject("booking", title, Bookings.Link(first.Id) + "?task=" + task.Id, first.OrgUnitId, first.HolderId);
    }

    public async Task<bool> MayDecideAsync(UserTaskInfo task, Guid authority, CancellationToken ct = default) =>
        (await WaitingAsync(task.InstanceId, false, ct)).FirstOrDefault() is { } first && authority != first.HolderId && authority != first.BookedBy;

    async Task ITaskSource.DecidedAsync(UserTaskInfo task, UserTaskInfo done, CancellationToken ct)
    {
        if ((await WaitingAsync(task.InstanceId, true, ct)).Count == 0 || await engine.GetInstanceAsync(task.InstanceId, ct) is not { Status: "completed" } instance)
        {
            return;
        }
        await DecidedAsync(instance.Id, instance.Outcome ?? InstanceOutcomes.Rejected, done.CompletedBy ?? context.RequireUser(), done.Comment, ct);
    }

    /// <summary>The resource's named approvers hear that a request waits; facilities administrators find it in their inbox.</summary>
    public async Task HandleAsync(UserTaskCreated message, CancellationToken ct)
    {
        if (!Handles(message.DefinitionKey) || (await WaitingAsync(message.InstanceId, false, ct)).FirstOrDefault() is not { } first)
        {
            return;
        }
        var to = first.ApproverIds.Where(a => a != first.HolderId && a != first.BookedBy).ToList();
        if (to.Count == 0)
        {
            return;
        }
        await notifications.SendAsync(new NotificationRequest(to, NotificationCategories.ApprovalWaiting, WaitingTemplate, Bookings.Link(first.Id) + "?task=" + message.TaskId,
            await DescribeAsync(first, (await WaitingAsync(message.InstanceId, false, ct)).Count, ct), DedupeKey: "task:" + message.TaskId.ToString("N")), ct);
    }
}
