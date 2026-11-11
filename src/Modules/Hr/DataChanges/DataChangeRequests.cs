using System.Text.Json;
using TeamBeats.Audit.Contracts;
using TeamBeats.Authorization.Contracts;
using TeamBeats.Entitlements.Contracts;
using TeamBeats.Localization;
using TeamBeats.Messaging;
using TeamBeats.Modules.Hr.Leave;
using TeamBeats.Modules.Identity.Contracts;
using TeamBeats.Modules.Integrations.Contracts;
using TeamBeats.Modules.Notifications.Contracts;
using TeamBeats.Modules.People.Contracts;
using TeamBeats.Modules.Workflow.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Primitives;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.Hr.DataChanges;

public static class DataChangeStatuses
{
    public const string Pending = "pending";
    public const string Approved = "approved";
    public const string Completed = "completed";
    public const string WriteFailed = "write_failed";
    public const string Rejected = "rejected";
    public const string Withdrawn = "withdrawn";

    public static readonly IReadOnlyList<string> All = [Pending, Approved, Completed, WriteFailed, Rejected, Withdrawn];
}

public sealed record DataChangeInput(string? Group, Dictionary<string, string?>? Values, string? Reason, Guid? DocumentId);

/// <summary>One changed field. <see cref="Value"/> is null once the change is written, refused or withdrawn.</summary>
public sealed record DataChangeValueView(string Key, string? Value);

public sealed record DataChangeRights(bool Withdraw, bool Decide, bool Complete, bool Document);

public sealed record DataChangeView(
    Guid Id,
    string Group,
    PersonRef Person,
    IReadOnlyList<DataChangeValueView> Fields,
    string? Reason,
    string? DocumentName,
    string Status,
    string? WriteBack,
    string? ExternalReference,
    DateTimeOffset CreatedAt,
    DateTimeOffset? DecidedAt,
    DateTimeOffset? CompletedAt,
    DataChangeRights Rights);

internal sealed record DataChangeRow(
    Guid Id, Guid UserId, Guid? OrgUnitId, string Group, string[] FieldKeys, Dictionary<string, string>? Values, string? Reason, Guid? DocumentId, string Status,
    string? WriteBack, string? ExternalReference, Guid? InstanceId, Guid? DecidedBy, DateTimeOffset? DecidedAt, DateTimeOffset? CompletedAt, DateTimeOffset CreatedAt);

/// <summary>
/// Employee data change requests (spec 022 US6, FR-019, FR-020). The person asks to change one group of their HR data
/// (address, emergency contact, bank details, marital status, dependants) with a document where the tenant asks for
/// one. HR officers covering the person approve it on the process engine, and nobody decides their own change. An
/// approved change is written to HR through spec 019 when a connection takes data changes, else HR enters it and marks
/// it done. The values are cleared as soon as the change is written, refused or withdrawn, and the document of a
/// sensitive group goes with them; the profile keeps showing HR's value until the next HR sync brings the new one.
/// A sensitive change notifies the person on every channel, so a change made by someone else is noticed.
/// </summary>
public sealed class DataChangeRequests(
    IDbSession session,
    ITenantContext context,
    IClock clock,
    IAuthorizationService authz,
    IEntitlementService entitlements,
    IAuditWriter audit,
    IProcessEngine engine,
    INotificationSender notifications,
    IUserDirectory users,
    IHrDataChange writeBack,
    DataChangeConfig config,
    LeaveLedger people)
{
    public const string ProcessKey = "hr.data_change";
    public const int MaxReasonLength = 500;

    public const string SubmittedTemplate = "hr.data_change_submitted";
    public const string CompletedTemplate = "hr.data_change_completed";
    public const string RejectedTemplate = "hr.data_change_rejected";
    public const string WriteFailedTemplate = "hr.data_change_write_failed";

    public static string Link(Guid id) => $"/my-details/requests/{id}";

    internal static Guid? Parse(string businessKey) => Guid.TryParseExact(businessKey, "N", out var id) ? id : null;

    private const string Columns =
        "id, user_id, org_unit_id, group_key, field_keys, values::text, reason, document_id, status, write_back, external_reference, instance_id, decided_by, decided_at, completed_at, created_at";

    private static DataChangeRow Read(Npgsql.NpgsqlDataReader r) => new(
        r.GetGuid(0), r.GetGuid(1), r.Get<Guid?>("org_unit_id"), r.GetString(3), r.GetFieldValue<string[]>(4),
        r.Get<string?>("values") is { } v ? JsonSerializer.Deserialize<Dictionary<string, string>>(v, JsonDefaults.Options) : null, r.Get<string?>("reason"),
        r.Get<Guid?>("document_id"), r.GetString(8), r.Get<string?>("write_back"), r.Get<string?>("external_reference"), r.Get<Guid?>("instance_id"),
        r.Get<Guid?>("decided_by"), r.Get<DateTimeOffset?>("decided_at"), r.Get<DateTimeOffset?>("completed_at"), r.GetFieldValue<DateTimeOffset>(15));

    internal async Task<DataChangeRow?> RowAsync(Guid id, bool forUpdate, CancellationToken ct) =>
        await session.QuerySingleOrDefaultAsync($"select {Columns} from hr.data_change_request where id = @id{(forUpdate ? " for update" : "")}", new { id }, Read, ct);

    private static bool Sensitive(string group) => DataChangeCatalogue.Find(group)?.Sensitive == true;

    private static string Category(string group) => Sensitive(group) ? NotificationCategories.SecurityNotice : NotificationCategories.RequestUpdate;

    private Task NotifyAsync(IReadOnlyList<Guid> to, string category, string template, DataChangeRow row, string stage, CancellationToken ct) =>
        notifications.SendAsync(new NotificationRequest(to, category, template, Link(row.Id), new Dictionary<string, string> { ["group"] = row.Group },
            DedupeKey: $"hr.data_change:{row.Id:N}:{stage}"), ct);

    // ---- asking ----

    public async Task<DataChangeView> CreateAsync(DataChangeInput input, CancellationToken ct)
    {
        await entitlements.DemandAsync(Capabilities.HrDataChange, ct);
        var me = context.RequireUser();
        if (await people.PersonAsync(me, ct) is not { Active: true } person)
        {
            throw AppException.Denied();
        }
        var errors = new ValidationErrors();
        var group = DataChangeCatalogue.Find(input.Group);
        var setting = group is null ? null : await config.GroupAsync(group.Key, ct);
        if (group is null || setting is not { Enabled: true })
        {
            throw AppException.Validation("group", "validation.data_change.group");
        }
        var (values, wrong) = DataChangeCatalogue.Check(group, input.Values);
        foreach (var key in wrong)
        {
            errors.Add($"values.{key}", "validation.data_change.value");
        }
        var reason = string.IsNullOrWhiteSpace(input.Reason) ? null : input.Reason.Trim();
        if (reason is not null && (reason.Length > MaxReasonLength || reason.Any(c => char.IsControl(c) && c is not '\n')))
        {
            errors.Add("reason", "validation.data_change.reason");
        }
        if (input.DocumentId is { } documentId)
        {
            if (await config.ProblemAsync(documentId, ct) is { } problem)
            {
                errors.Add("documentId", problem);
            }
        }
        else if (setting.Document)
        {
            errors.Add("documentId", "validation.data_change.document_required");
        }
        errors.ThrowIfAny();

        await session.ExecAsync("select pg_advisory_xact_lock(hashtext('hr.data_change:' || @me::text))", new { me }, ct);
        if (await session.ScalarAsync<long>("select count(*) from hr.data_change_request where user_id = @me and group_key = @group and status in ('pending', 'approved')",
                new { me, group = group.Key }, ct) > 0)
        {
            throw AppException.Conflict("conflict.data_change.open");
        }
        var id = Ids.New();
        var now = clock.UtcNow;
        await session.ExecAsync(
            """
            insert into hr.data_change_request (tenant_id, id, user_id, org_unit_id, group_key, field_keys, values, reason, document_id, status, created_at, updated_at)
            values (platform.current_tenant_id(), @id, @me, @unit, @group, @keys, @values, @reason, @documentId, 'pending', @now, @now)
            """, new { id, me, unit = person.PrimaryUnit, group = group.Key, keys = values.Keys.ToArray(), values = Jsonb.From(values), reason, documentId = input.DocumentId, now },
            ct);
        if (input.DocumentId is { } attached)
        {
            await config.LinkAsync(attached, id, ct);
        }
        var instance = await engine.StartAsync(await RouteAsync(ct), id.ToString("N"), person.PrimaryUnit, me, [me], me, ct: ct);
        await session.ExecAsync("update hr.data_change_request set instance_id = @instance where id = @id", new { id, instance = instance.Id }, ct);
        // Field names only, never the values (section 13).
        await audit.WriteAsync(new AuditEntry(AuditActions.HrDataChangeRequested, "data_change_request", id.ToString(), TargetOrgUnitId: person.PrimaryUnit,
            Changes: new { group = group.Key, fields = values.Keys, document = input.DocumentId is not null }), ct);
        var row = (await RowAsync(id, false, ct))!;
        if (group.Sensitive)
        {
            await NotifyAsync([me], NotificationCategories.SecurityNotice, SubmittedTemplate, row, "submitted", ct);
        }
        return await ViewAsync(row, ct);
    }

    /// <summary>One HR step: HR officers covering the person's unit decide.</summary>
    private async Task<string> RouteAsync(CancellationToken ct)
    {
        if (await engine.GetDefinitionAsync(ProcessKey, null, ct) is null)
        {
            await engine.PublishRouteAsync(ProcessKey, RouteSources.DataChangeRoute,
                new RouteSpec("data change approval", [new RouteStep("hr", "HR", new CandidateRule(Permission: Permissions.HrManage))]), ct);
        }
        return ProcessKey;
    }

    public async Task<IReadOnlyList<DataChangeView>> MineAsync(CancellationToken ct)
    {
        await entitlements.DemandAsync(Capabilities.HrDataChange, ct);
        var rows = await session.QueryAsync($"select {Columns} from hr.data_change_request where user_id = @me order by created_at desc limit 100",
            new { me = context.RequireUser() }, Read, ct);
        var list = new List<DataChangeView>();
        foreach (var row in rows)
        {
            list.Add(await ViewAsync(row, ct));
        }
        return list;
    }

    /// <summary>The changes of the people the HR officer covers, newest first.</summary>
    public async Task<IReadOnlyList<DataChangeView>> ListAsync(string? status, CancellationToken ct)
    {
        await entitlements.DemandAsync(Capabilities.HrDataChange, ct);
        var coverage = await authz.CoverageAsync(Permissions.HrManage, ct);
        if (!coverage.Any)
        {
            throw AppException.Denied();
        }
        if (status is not null && !DataChangeStatuses.All.Contains(status))
        {
            throw AppException.Validation("status", "validation.data_change.status");
        }
        var rows = await session.QueryAsync(
            $"select {Columns} from hr.data_change_request where (@status::text is null or status = @status) order by created_at desc limit 500", new { status }, Read, ct);
        var list = new List<DataChangeView>();
        foreach (var row in rows.Where(r => r.OrgUnitId is null ? coverage.WholeTenant : coverage.Covers(r.OrgUnitId)).Take(200))
        {
            list.Add(await ViewAsync(row, ct));
        }
        return list;
    }

    public async Task<DataChangeView> GetAsync(Guid id, CancellationToken ct)
    {
        await entitlements.DemandAsync(Capabilities.HrDataChange, ct);
        if (await RowAsync(id, false, ct) is not { } row || !await MayReadAsync(row, ct))
        {
            throw AppException.NotFound();
        }
        return await ViewAsync(row, ct);
    }

    /// <summary>The person, the holder of its approval task and HR officers covering the person.</summary>
    private async Task<bool> MayReadAsync(DataChangeRow row, CancellationToken ct) =>
        row.UserId == context.RequireUser() || await DecidingTaskAsync(row, ct) is not null || await HrCoversAsync(row, ct);

    private async Task<bool> HrCoversAsync(DataChangeRow row, CancellationToken ct) =>
        await people.PersonAsync(row.UserId, ct) is { } person && await people.HrCoversAsync(person, ct);

    internal async Task<UserTaskInfo?> DecidingTaskAsync(DataChangeRow row, CancellationToken ct)
    {
        if (row.InstanceId is not { } instance || row.Status != DataChangeStatuses.Pending || row.UserId == context.RequireUser())
        {
            return null;
        }
        foreach (var task in (await engine.InstanceTasksAsync(instance, ct)).Where(t => t.Status == "open"))
        {
            if (await engine.MyTaskAsync(task.Id, ct) is { } mine)
            {
                return mine;
            }
        }
        return null;
    }

    public async Task<(Stream Content, string ContentType, string FileName)> DocumentAsync(Guid id, CancellationToken ct)
    {
        await entitlements.DemandAsync(Capabilities.HrDataChange, ct);
        if (await RowAsync(id, false, ct) is not { } row || !await MayReadAsync(row, ct) || row.DocumentId is not { } document)
        {
            throw AppException.NotFound();
        }
        return await config.OpenAsync(document, ct);
    }

    public async Task<DataChangeView> WithdrawAsync(Guid id, CancellationToken ct)
    {
        await entitlements.DemandAsync(Capabilities.HrDataChange, ct);
        if (await RowAsync(id, true, ct) is not { } row || row.UserId != context.RequireUser())
        {
            throw AppException.NotFound();
        }
        if (row.Status != DataChangeStatuses.Pending)
        {
            throw AppException.Conflict("conflict.data_change.not_pending");
        }
        if (row.InstanceId is { } instance)
        {
            await engine.WithdrawAsync(instance, ct);
        }
        await EndAsync(row, DataChangeStatuses.Withdrawn, null, ct);
        return await ViewAsync((await RowAsync(id, false, ct))!, ct);
    }

    /// <summary>HR entered an approved change in the HR system by hand (no connection takes data changes): the values go.</summary>
    public async Task<DataChangeView> CompleteAsync(Guid id, CancellationToken ct)
    {
        await entitlements.DemandAsync(Capabilities.HrDataChange, ct);
        if (await RowAsync(id, true, ct) is not { } row || row.UserId == context.RequireUser() || !await HrCoversAsync(row, ct))
        {
            throw AppException.NotFound();
        }
        if (row.Status != DataChangeStatuses.Approved || row.WriteBack != "manual")
        {
            throw AppException.Conflict("conflict.data_change.not_manual");
        }
        await CompletedAsync(row, null, true, ct);
        return await ViewAsync((await RowAsync(id, false, ct))!, ct);
    }

    /// <summary>The approval ended: an approved change goes to HR, a refused one is ended and its values cleared.</summary>
    internal async Task DecidedAsync(DataChangeRow row, ProcessInstanceInfo instance, Guid decider, CancellationToken ct)
    {
        var now = clock.UtcNow;
        await session.ExecAsync("update hr.data_change_request set decided_by = @decider, decided_at = @now, updated_at = @now where id = @id",
            new { id = row.Id, decider, now }, ct);
        if (instance.Outcome != InstanceOutcomes.Approved)
        {
            await EndAsync(row, DataChangeStatuses.Rejected, null, ct);
            await audit.WriteAsync(new AuditEntry(AuditActions.HrDataChangeRejected, "data_change_request", row.Id.ToString(), TargetOrgUnitId: row.OrgUnitId,
                Changes: new { group = row.Group, fields = row.FieldKeys, approver = decider, outcome = instance.Outcome }), ct);
            await NotifyAsync([row.UserId], Category(row.Group), RejectedTemplate, row, "rejected", ct);
            return;
        }
        await audit.WriteAsync(new AuditEntry(AuditActions.HrDataChangeApproved, "data_change_request", row.Id.ToString(), TargetOrgUnitId: row.OrgUnitId,
            Changes: new { group = row.Group, fields = row.FieldKeys, approver = decider }), ct);
        var state = await writeBack.HandOffAsync(new HrDataChangeHandOffRequest(row.Id, row.UserId, row.Group, row.Values ?? []), ct);
        await session.ExecAsync("update hr.data_change_request set status = 'approved', write_back = @mode, updated_at = @now where id = @id",
            new { id = row.Id, mode = state == HrDataChangeHandOffStates.Unavailable ? "manual" : "hr", now }, ct);
        var current = (await RowAsync(row.Id, true, ct))!;
        if (state == HrDataChangeHandOffStates.Recorded)
        {
            await CompletedAsync(current, null, false, ct);
        }
        else if (state == HrDataChangeHandOffStates.Failed)
        {
            await WriteFailedAsync(current, ct);
        }
    }

    /// <summary>HR answered the write-back (spec 019): recorded completes the change, failed asks HR to enter it.</summary>
    internal async Task HandOffChangedAsync(HrDataChangeHandOffChanged message, CancellationToken ct)
    {
        if (await RowAsync(message.RequestId, true, ct) is not { Status: DataChangeStatuses.Approved, WriteBack: "hr" } row)
        {
            return;
        }
        if (message.State == HrDataChangeHandOffStates.Recorded)
        {
            await CompletedAsync(row, message.ExternalReference, false, ct);
        }
        else if (message.State == HrDataChangeHandOffStates.Failed)
        {
            await WriteFailedAsync(row, ct);
        }
    }

    private async Task CompletedAsync(DataChangeRow row, string? reference, bool manual, CancellationToken ct)
    {
        await EndAsync(row, DataChangeStatuses.Completed, reference, ct);
        await audit.WriteAsync(new AuditEntry(AuditActions.HrDataChangeWrittenBack, "data_change_request", row.Id.ToString(), TargetOrgUnitId: row.OrgUnitId,
            Changes: new { group = row.Group, fields = row.FieldKeys, manual, reference }), ct);
        await NotifyAsync([row.UserId], Category(row.Group), CompletedTemplate, row, "completed", ct);
    }

    /// <summary>HR refused the write or never answered: the values are gone, so the person asks again after HR looks.</summary>
    private async Task WriteFailedAsync(DataChangeRow row, CancellationToken ct)
    {
        await EndAsync(row, DataChangeStatuses.WriteFailed, null, ct);
        await audit.WriteAsync(new AuditEntry(AuditActions.HrDataChangeWriteFailed, "data_change_request", row.Id.ToString(), TargetOrgUnitId: row.OrgUnitId,
            Changes: new { group = row.Group, fields = row.FieldKeys }), ct);
        var to = row.DecidedBy is { } decider && decider != row.UserId ? new[] { row.UserId, decider } : [row.UserId];
        await NotifyAsync(to, NotificationCategories.RequestUpdate, WriteFailedTemplate, row, "write_failed", ct);
    }

    /// <summary>Ends a change: the values are cleared, and a sensitive group's document is deleted once written.</summary>
    private async Task EndAsync(DataChangeRow row, string status, string? reference, CancellationToken ct)
    {
        var now = clock.UtcNow;
        await session.ExecAsync(
            """
            update hr.data_change_request set status = @status, values = null, external_reference = coalesce(@reference, external_reference),
              completed_at = case when @status = 'completed' then @now else completed_at end, updated_at = @now
            where id = @id
            """, new { id = row.Id, status, reference, now }, ct);
        if (row.DocumentId is { } document && (Sensitive(row.Group) || status is DataChangeStatuses.Withdrawn or DataChangeStatuses.Rejected))
        {
            await config.DeleteAsync(document, ct);
        }
    }

    private async Task<DataChangeView> ViewAsync(DataChangeRow row, CancellationToken ct)
    {
        var name = (await users.GetAsync(row.UserId, ct))?.DisplayName ?? row.UserId.ToString();
        var me = context.RequireUser();
        var deciding = await DecidingTaskAsync(row, ct) is not null;
        var hrCovers = row.UserId != me && await HrCoversAsync(row, ct);
        var fields = row.FieldKeys.Select(k => new DataChangeValueView(k, row.Values?.GetValueOrDefault(k))).ToList();
        var documentName = await config.NameAsync(row.DocumentId, ct);
        var rights = new DataChangeRights(row.UserId == me && row.Status == DataChangeStatuses.Pending, deciding,
            hrCovers && row.Status == DataChangeStatuses.Approved && row.WriteBack == "manual", documentName is not null);
        return new DataChangeView(row.Id, row.Group, new PersonRef(row.UserId, name), fields, row.Reason, documentName, row.Status, row.WriteBack, row.ExternalReference,
            row.CreatedAt, row.DecidedAt, row.CompletedAt, rights);
    }
}

/// <summary>
/// Data changes in the spec 016 task inbox (FR-026): HR officers covering the person decide; nobody decides their own
/// change, directly or as a delegate (section 6).
/// </summary>
public sealed class DataChangeTaskSource(ITenantContext context, IProcessEngine engine, DataChangeRequests requests, ILocalizer localizer)
    : ITaskSource, IMessageHandler<HrDataChangeHandOffChanged>
{
    public bool Handles(string definitionKey) => definitionKey == DataChangeRequests.ProcessKey;

    private async Task<DataChangeRow?> WaitingAsync(UserTaskInfo task, bool forUpdate, CancellationToken ct) =>
        DataChangeRequests.Parse(task.BusinessKey) is { } id && await requests.RowAsync(id, forUpdate, ct) is { Status: DataChangeStatuses.Pending } row
            && row.InstanceId == task.InstanceId
            ? row
            : null;

    public async Task<TaskSubject?> DescribeAsync(UserTaskInfo task, CancellationToken ct = default)
    {
        if (await WaitingAsync(task, false, ct) is not { } row)
        {
            return null;
        }
        var title = Locales.LaunchComplete.ToDictionary(l => l, l => localizer.Get($"hr.data_change.task.{row.Group}", l));
        return new TaskSubject("data_change", title, DataChangeRequests.Link(row.Id) + "?task=" + task.Id, row.OrgUnitId, row.UserId);
    }

    public async Task<bool> MayDecideAsync(UserTaskInfo task, Guid authority, CancellationToken ct = default) =>
        await WaitingAsync(task, false, ct) is { } row && authority != row.UserId;

    public async Task DecidedAsync(UserTaskInfo task, UserTaskInfo done, CancellationToken ct = default)
    {
        if (await WaitingAsync(task, true, ct) is not { } row || await engine.GetInstanceAsync(task.InstanceId, ct) is not { Status: "completed" } instance)
        {
            return;
        }
        await requests.DecidedAsync(row, instance, done.CompletedBy ?? context.RequireUser(), ct);
    }

    public Task HandleAsync(HrDataChangeHandOffChanged message, CancellationToken ct) => requests.HandOffChangedAsync(message, ct);
}
