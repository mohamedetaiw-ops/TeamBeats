using System.Text.Json;
using System.Text.RegularExpressions;
using TeamBeats.Audit.Contracts;
using TeamBeats.Authorization.Contracts;
using TeamBeats.Entitlements.Contracts;
using TeamBeats.Messaging;
using TeamBeats.Modules.Documents.Contracts;
using TeamBeats.Modules.Identity.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Primitives;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.Documents;

public static class RetentionTriggers
{
    public const string Created = "created";
    public const string Modified = "modified";
    public const string Effective = "effective";
    public const string Superseded = "superseded";
    public const string Field = "field";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal) { Created, Modified, Effective, Superseded, Field };
}

public static class RetentionLabelActions
{
    public const string Delete = "delete";
    public const string Review = "review";
    public const string Archive = "archive";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal) { Delete, Review, Archive };
}

public sealed record LabelRequest(
    IReadOnlyDictionary<string, string>? Names,
    int? PeriodMonths,
    string? Trigger,
    string? TriggerField,
    string? Action,
    string? Status = null,
    int? Version = null,
    bool Reapply = false);

public sealed record LabelView(Guid Id, IReadOnlyDictionary<string, string> Names, int PeriodMonths, string Trigger, string? TriggerField, string Action, string Status,
    int Documents, int Version);

public sealed record LabelChoice(Guid? LabelId);

public sealed record RecordsTypeView(Guid Id, IReadOnlyDictionary<string, string> Names, Guid? LabelId, IReadOnlyList<string> DateFields);

public sealed record RecordsLibraryView(Guid Id, IReadOnlyDictionary<string, string> Names, string Scope, Guid? LabelId, IReadOnlyList<RecordsTypeView> Types);

public sealed record DocumentRetentionView(Guid DocumentId, Guid? LabelId, string? Source, DateTimeOffset? EndsAt, string? Disposition, bool Held);

public sealed record DispositionItem(Guid DocumentId, Guid LibraryId, string Name, string? Number, Guid LabelId, IReadOnlyDictionary<string, string> LabelNames,
    DateTimeOffset? EndedAt, DateTimeOffset DueAt, bool Held);

public sealed record DispositionDecision(string? Decision, int? KeepMonths);

public sealed record DispositionRecordView(Guid Id, Guid DocumentId, string? Number, IReadOnlyDictionary<string, string> LabelNames, int Versions, string Mode,
    Guid? ApprovedBy, DateTimeOffset DisposedAt);

public sealed record HoldRequest(string? Name, string? Reason, Guid? CustodianId, DateTimeOffset? From, DateTimeOffset? To, Guid? LibraryId, Guid? FolderId,
    Guid? DocumentId, bool IncludeAudit = false);

public sealed record HoldView(Guid Id, string Name, string Reason, Guid? CustodianId, DateTimeOffset? From, DateTimeOffset? To, Guid? LibraryId, Guid? FolderId,
    Guid? DocumentId, bool IncludeAudit, string Status, DateTimeOffset CreatedAt, Guid? CreatedBy, DateTimeOffset? ReleasedAt, int Documents);

public sealed record HeldDocument(Guid Id, Guid LibraryId, string Name, string? Number);

public sealed record HoldDetail(HoldView Hold, IReadOnlyList<HeldDocument> Documents);

/// <summary>
/// Records management on TeamBeats libraries (spec 011 R2 US6 and US7). Records managers define retention labels with a
/// period, a trigger and an action, and apply them by library, document type or by hand; the longer label wins when a
/// library and a type both carry one. When a period ends the document enters disposition review, is archived, or is
/// deleted without review, leaving a content-free disposition record. Legal holds by custodian and date range, library,
/// folder or document stop every deletion path until released; after a release, documents whose period passed while
/// held go to review instead of being deleted at once. Holds, and that a document is held, are for records managers only.
/// </summary>
public sealed partial class Records(
    IDbSession session,
    ITenantContext context,
    IAuthorizationService authz,
    IEntitlementService entitlements,
    Libraries libraries,
    DocumentVersions versions,
    IUserDirectory users,
    IAuditWriter audit,
    IOutbox outbox,
    IClock clock)
{
    public const int MaxPeriodMonths = 1200;
    public const int MaxHeldShown = 200;
    public const int MaxReasonLength = 2000;
    public const int MaxHoldNameLength = 200;
    public const int MaxRecords = 500;

    [GeneratedRegex("^[a-z][a-z0-9_]{0,63}$")]
    private static partial Regex FieldKey();

    private async Task RequireAsync(string capability, CancellationToken ct)
    {
        await entitlements.DemandAsync(capability, ct);
        context.RequireUser();
        await authz.DemandAsync(Permissions.RecordsManage, AuthzTarget.Tenant, ct);
    }

    // Labels (FR-013)

    private const string LabelColumns = "l.id, l.names::text, l.period_months, l.trigger, l.trigger_field, l.action, l.status, l.row_version";

    private static LabelView ReadLabel(Npgsql.NpgsqlDataReader r) => new(r.GetGuid(0), DocJson.Read<Dictionary<string, string>>(r.GetString(1)), r.GetInt32(2),
        r.GetString(3), r.Get<string>("trigger_field"), r.GetString(5), r.GetString(6), r.GetInt32(8), r.GetInt32(7));

    public async Task<IReadOnlyList<LabelView>> LabelsAsync(CancellationToken ct)
    {
        await RequireAsync(Capabilities.RecordsRetention, ct);
        return await session.QueryAsync(
            $"""
            select {LabelColumns},
              (select count(*)::int from documents.document d where d.retention_label_id = l.id and d.deleted_at is null) as documents
            from documents.retention_label l order by l.status, l.created_at, l.id
            """, null, ReadLabel, ct);
    }

    private async Task<LabelView> LabelAsync(Guid id, CancellationToken ct) =>
        await session.QuerySingleOrDefaultAsync(
            $"""
            select {LabelColumns},
              (select count(*)::int from documents.document d where d.retention_label_id = l.id and d.deleted_at is null) as documents
            from documents.retention_label l where l.id = @id
            """, new { id }, ReadLabel, ct) ?? throw AppException.NotFound();

    private static void Validate(LabelRequest request, ValidationErrors errors)
    {
        Libraries.Names(request.Names, "names", errors);
        if (request.PeriodMonths is not (>= 1 and <= MaxPeriodMonths))
        {
            errors.Add("periodMonths", "validation.records.period");
        }
        if (request.Trigger is null || !RetentionTriggers.All.Contains(request.Trigger))
        {
            errors.Add("trigger", "validation.records.trigger");
        }
        else if ((request.Trigger == RetentionTriggers.Field) != (request.TriggerField is not null)
            || (request.TriggerField is { } field && !FieldKey().IsMatch(field)))
        {
            errors.Add("triggerField", "validation.records.trigger_field");
        }
        if (request.Action is null || !RetentionLabelActions.All.Contains(request.Action))
        {
            errors.Add("action", "validation.records.action");
        }
        if (request.Status is not (null or "active" or "retired"))
        {
            errors.Add("status", "validation.records.status");
        }
    }

    public async Task<LabelView> CreateLabelAsync(LabelRequest request, CancellationToken ct)
    {
        await RequireAsync(Capabilities.RecordsRetention, ct);
        var errors = new ValidationErrors();
        Validate(request, errors);
        errors.ThrowIfAny();
        var id = Ids.New();
        var now = clock.UtcNow;
        await session.ExecAsync(
            """
            insert into documents.retention_label (tenant_id, id, names, period_months, trigger, trigger_field, action, created_at, created_by, updated_at)
            values (platform.current_tenant_id(), @id, @names, @months, @trigger, @field, @action, @now, @by, @now)
            """, new
            {
                id, names = new Jsonb(DocJson.Write(Libraries.Trimmed(request.Names!))), months = request.PeriodMonths, trigger = request.Trigger, field = request.TriggerField,
                action = request.Action, now, by = context.ActorId,
            }, ct);
        await audit.WriteAsync(new AuditEntry(AuditActions.RecordsLabelCreated, "retention_label", id.ToString(),
            Changes: new { names = request.Names, periodMonths = request.PeriodMonths, trigger = request.Trigger, triggerField = request.TriggerField, action = request.Action }), ct);
        return await LabelAsync(id, ct);
    }

    /// <summary>
    /// Changes a label. A longer period reaches every document carrying the label at once; a shorter period or another
    /// action reaches documents labelled before only when <see cref="LabelRequest.Reapply"/> is set (US6 scenario 5).
    /// Retiring a label stops it being applied anew; documents keep it.
    /// </summary>
    public async Task<LabelView> UpdateLabelAsync(Guid id, LabelRequest request, CancellationToken ct)
    {
        await RequireAsync(Capabilities.RecordsRetention, ct);
        var before = await session.QuerySingleOrDefaultAsync($"select {LabelColumns}, 0 from documents.retention_label l where l.id = @id for update", new { id }, ReadLabel, ct)
            ?? throw AppException.NotFound();
        var errors = new ValidationErrors();
        Validate(request, errors);
        errors.ThrowIfAny();
        if (request.Version != before.Version)
        {
            throw AppException.Conflict("conflict.records.stale");
        }
        var now = clock.UtcNow;
        await session.ExecAsync(
            """
            update documents.retention_label set names = @names, period_months = @months, trigger = @trigger, trigger_field = @field, action = @action,
              status = coalesce(@status, status), updated_at = @now, row_version = row_version + 1
            where id = @id
            """, new
            {
                id, names = new Jsonb(DocJson.Write(Libraries.Trimmed(request.Names!))), months = request.PeriodMonths, trigger = request.Trigger, field = request.TriggerField,
                action = request.Action, status = request.Status, now,
            }, ct);
        var reaches = request.Reapply
            ? await session.ExecAsync(
                "update documents.document set retention_months = @months, retention_action = @action, disposition = null, disposition_due_at = null where retention_label_id = @id and deleted_at is null",
                new { id, months = request.PeriodMonths, action = request.Action }, ct)
            : await session.ExecAsync(
                "update documents.document set retention_months = @months where retention_label_id = @id and retention_months < @months and deleted_at is null",
                new { id, months = request.PeriodMonths }, ct);
        await audit.WriteAsync(new AuditEntry(AuditActions.RecordsLabelChanged, "retention_label", id.ToString(),
            Changes: new
            {
                before = new { before.Names, before.PeriodMonths, before.Trigger, before.TriggerField, before.Action, before.Status },
                after = new { request.Names, request.PeriodMonths, request.Trigger, request.TriggerField, request.Action, status = request.Status ?? before.Status },
                reapplied = request.Reapply, documents = reaches,
            }), ct);
        return await LabelAsync(id, ct);
    }

    private async Task<Guid?> ActiveLabelAsync(Guid? labelId, CancellationToken ct)
    {
        if (labelId is { } id && !await session.ScalarAsync<bool>("select exists (select 1 from documents.retention_label where id = @id and status = 'active')", new { id }, ct))
        {
            throw AppException.Validation("labelId", "validation.records.label_unknown");
        }
        return labelId;
    }

    // Applying labels

    /// <summary>Every library of the tenant with its label, and its document types with their labels and date fields.</summary>
    public async Task<IReadOnlyList<RecordsLibraryView>> LibrariesAsync(CancellationToken ct)
    {
        await RequireAsync(Capabilities.RecordsRetention, ct);
        var rows = await session.QueryAsync("select id, names::text, scope, retention_label_id from documents.library where status = 'active' order by created_at, id", null,
            r => (Id: r.GetGuid(0), Names: DocJson.Read<Dictionary<string, string>>(r.GetString(1)), Scope: r.GetString(2), Label: r.Get<Guid?>("retention_label_id")), ct);
        var labels = (await session.QueryAsync("select id, retention_label_id from documents.document_type where retention_label_id is not null", null,
            r => (Id: r.GetGuid(0), Label: r.GetGuid(1)), ct)).ToDictionary(r => r.Id, r => r.Label);
        var list = new List<RecordsLibraryView>();
        foreach (var row in rows)
        {
            var types = (await libraries.TypesAsync(row.Id, ct)).Where(t => t.Status == "active")
                .Select(t => new RecordsTypeView(t.Id, t.Names, labels.TryGetValue(t.Id, out var l) ? l : null,
                    t.Fields.Where(f => f.Kind == FieldKinds.Date).Select(f => f.Key).ToList()))
                .ToList();
            list.Add(new RecordsLibraryView(row.Id, row.Names, row.Scope, row.Label, types));
        }
        return list;
    }

    public async Task SetLibraryLabelAsync(Guid libraryId, LabelChoice choice, CancellationToken ct)
    {
        await RequireAsync(Capabilities.RecordsRetention, ct);
        var label = await ActiveLabelAsync(choice.LabelId, ct);
        if (await session.ExecAsync("update documents.library set retention_label_id = @label where id = @libraryId", new { libraryId, label }, ct) == 0)
        {
            throw AppException.NotFound();
        }
        await RecordsSql.InheritAsync(session, clock.UtcNow, ct, library: libraryId);
        await audit.WriteAsync(new AuditEntry(AuditActions.RecordsLabelApplied, Securables.Library, libraryId.ToString(),
            Changes: new { label, by = "library", count = await CountAsync(label, "library_id", libraryId, ct) }), ct);
    }

    public async Task SetTypeLabelAsync(Guid typeId, LabelChoice choice, CancellationToken ct)
    {
        await RequireAsync(Capabilities.RecordsRetention, ct);
        var label = await ActiveLabelAsync(choice.LabelId, ct);
        if (await session.ExecAsync("update documents.document_type set retention_label_id = @label where id = @typeId", new { typeId, label }, ct) == 0)
        {
            throw AppException.NotFound();
        }
        await RecordsSql.InheritAsync(session, clock.UtcNow, ct, type: typeId);
        await audit.WriteAsync(new AuditEntry(AuditActions.RecordsLabelApplied, "doc_type", typeId.ToString(),
            Changes: new { label, by = "type", count = await CountAsync(label, "type_id", typeId, ct) }), ct);
    }

    private async Task<int> CountAsync(Guid? label, string column, Guid value, CancellationToken ct) =>
        label is null ? 0 : await session.ScalarAsync<int>(
            $"select count(*)::int from documents.document where {column} = @value and retention_label_id = @label and deleted_at is null", new { value, label }, ct);

    /// <summary>Labels one document by hand, or with no label gives it back to its library and type.</summary>
    public async Task<DocumentRetentionView> SetDocumentLabelAsync(Guid documentId, LabelChoice choice, CancellationToken ct)
    {
        await RequireAsync(Capabilities.RecordsRetention, ct);
        var label = await ActiveLabelAsync(choice.LabelId, ct);
        var now = clock.UtcNow;
        var changed = label is { } id
            ? await session.ExecAsync(
                """
                update documents.document d set retention_label_id = @id, retention_source = 'manual', retention_months = l.period_months, retention_action = l.action,
                  labelled_at = @now, disposition = null, disposition_due_at = null
                from documents.retention_label l where l.id = @id and d.id = @documentId and d.deleted_at is null
                """, new { id, documentId, now }, ct)
            : await session.ExecAsync(
                "update documents.document set retention_label_id = null, retention_source = null, retention_months = null, retention_action = null, labelled_at = null, disposition = null, disposition_due_at = null where id = @documentId and deleted_at is null",
                new { documentId }, ct);
        if (changed == 0)
        {
            throw AppException.NotFound();
        }
        if (label is null)
        {
            await RecordsSql.InheritAsync(session, now, ct, ids: [documentId]);
        }
        await audit.WriteAsync(new AuditEntry(AuditActions.RecordsLabelApplied, Securables.Document, documentId.ToString(),
            Changes: new { label, by = "manual", count = 1 }), ct);
        return await RetentionOfAsync(documentId, ct);
    }

    /// <summary>A document's label, when its period ends and whether it is held, for records managers.</summary>
    public async Task<DocumentRetentionView> DocumentAsync(Guid documentId, CancellationToken ct)
    {
        await RequireAsync(Capabilities.RecordsRetention, ct);
        return await RetentionOfAsync(documentId, ct);
    }

    private async Task<DocumentRetentionView> RetentionOfAsync(Guid documentId, CancellationToken ct) =>
        await session.QuerySingleOrDefaultAsync(
            $"""
            select d.id, d.retention_label_id, d.retention_source, {RecordsSql.EndsAt("d")} as ends_at, d.disposition, {RecordsSql.Held("d")} as held
            from documents.document d where d.id = @documentId and d.deleted_at is null
            """, new { documentId },
            r => new DocumentRetentionView(r.GetGuid(0), r.Get<Guid?>("retention_label_id"), r.Get<string>("retention_source"), r.Get<DateTimeOffset?>("ends_at"),
                r.Get<string>("disposition"), r.GetBoolean(5)), ct)
        ?? throw AppException.NotFound();

    // Disposition (US6 scenarios 1 to 3, FR-014)

    public async Task<IReadOnlyList<DispositionItem>> QueueAsync(CancellationToken ct)
    {
        await RequireAsync(Capabilities.RecordsRetention, ct);
        return await session.QueryAsync(
            $"""
            select d.id, d.library_id, d.name, d.number, d.retention_label_id, l.names::text, {RecordsSql.EndsAt("d")} as ended_at, d.disposition_due_at, {RecordsSql.Held("d")} as held
            from documents.document d join documents.retention_label l on l.id = d.retention_label_id
            where d.disposition = 'review' and d.deleted_at is null
            order by d.disposition_due_at, d.id limit {MaxRecords}
            """, null,
            r => new DispositionItem(r.GetGuid(0), r.GetGuid(1), r.GetString(2), r.Get<string>("number"), r.GetGuid(4), DocJson.Read<Dictionary<string, string>>(r.GetString(5)),
                r.Get<DateTimeOffset?>("ended_at"), r.GetFieldValue<DateTimeOffset>(7), r.GetBoolean(8)), ct);
    }

    /// <summary>
    /// The records manager's decision on a document in review: dispose deletes it with all its versions and keeps a
    /// disposition record; keep holds it for more months, after which it comes back to review. A held document cannot
    /// be disposed.
    /// </summary>
    public async Task DecideAsync(Guid documentId, DispositionDecision decision, CancellationToken ct)
    {
        await RequireAsync(Capabilities.RecordsRetention, ct);
        var user = context.RequireUser();
        var row = await session.QuerySingleOrDefaultAsync(
            $"select d.library_id, {RecordsSql.Held("d")} from documents.document d where d.id = @documentId and d.disposition = 'review' and d.deleted_at is null for update of d",
            new { documentId }, r => (Library: r.GetGuid(0), Held: r.GetBoolean(1)), ct);
        if (row == default)
        {
            throw AppException.NotFound();
        }
        switch (decision.Decision)
        {
            case "dispose":
                if (row.Held)
                {
                    await audit.WriteAsync(new AuditEntry(AuditActions.RecordsDeletionBlocked, Securables.Document, documentId.ToString(), AuditOutcome.Denied,
                        Changes: new { library = row.Library, path = "disposition" }), ct);
                    await session.CommitAsync(ct);
                    throw AppException.Conflict("conflict.records.held");
                }
                await DisposeAsync(documentId, "reviewed", user, ct);
                break;
            case "keep" when decision.KeepMonths is >= 1 and <= MaxPeriodMonths:
                // The period now ends the given number of months from today.
                await session.ExecAsync(
                    $"""
                    update documents.document d set disposition = null, disposition_due_at = null, deletion_blocked_at = null,
                      retention_months = coalesce((select (extract(year from age(@now, t.at)) * 12 + extract(month from age(@now, t.at)))::int + 1
                        from (select {RecordsSql.TriggerAt("d")} as at) t), 0) + @months
                    where d.id = @documentId
                    """, new { documentId, now = clock.UtcNow, months = decision.KeepMonths }, ct);
                await audit.WriteAsync(new AuditEntry(AuditActions.RecordsDispositionKept, Securables.Document, documentId.ToString(),
                    Changes: new { library = row.Library, months = decision.KeepMonths }), ct);
                break;
            default:
                throw AppException.Validation("decision", "validation.records.decision");
        }
    }

    private async Task DisposeAsync(Guid documentId, string mode, Guid? approver, CancellationToken ct)
    {
        var row = await session.QuerySingleOrDefaultAsync(
            """
            select d.library_id, d.number, d.inherits, d.retention_label_id, coalesce(l.names::text, '{}')
            from documents.document d left join documents.retention_label l on l.id = d.retention_label_id where d.id = @documentId
            """, new { documentId },
            r => (Library: r.GetGuid(0), Number: r.Get<string>("number"), Inherits: r.GetBoolean(2), Label: r.Get<Guid?>("retention_label_id"), Names: r.GetString(4)), ct);
        var count = await versions.EraseAsync(documentId, row.Inherits, ct);
        await session.ExecAsync(
            """
            insert into documents.disposition_record (tenant_id, id, document_id, library_id, number, label_id, label_names, versions, mode, approved_by, disposed_at)
            values (platform.current_tenant_id(), @id, @documentId, @library, @number, @label, @names, @count, @mode, @approver, @now)
            """, new { id = Ids.New(), documentId, library = row.Library, number = row.Number, label = row.Label, names = new Jsonb(row.Names), count, mode, approver, now = clock.UtcNow }, ct);
        await audit.WriteAsync(new AuditEntry(AuditActions.RecordsDispositionApproved, Securables.Document, documentId.ToString(),
            Changes: new { library = row.Library, number = row.Number, label = row.Label, versions = count, mode }), ct);
        await outbox.EnqueueAsync(new DocumentDisposed(documentId, row.Library), ct);
    }

    public async Task<IReadOnlyList<DispositionRecordView>> DispositionRecordsAsync(CancellationToken ct)
    {
        await RequireAsync(Capabilities.RecordsRetention, ct);
        return await session.QueryAsync(
            $"select id, document_id, number, label_names::text, versions, mode, approved_by, disposed_at from documents.disposition_record order by disposed_at desc, id limit {MaxRecords}",
            null, r => new DispositionRecordView(r.GetGuid(0), r.GetGuid(1), r.Get<string>("number"), DocJson.Read<Dictionary<string, string>>(r.GetString(3)), r.GetInt32(4),
                r.GetString(5), r.Get<Guid?>("approved_by"), r.GetFieldValue<DateTimeOffset>(7)), ct);
    }

    // Legal holds (US7, FR-015)

    private const string HoldColumns =
        "h.id, h.name, h.reason, h.custodian_id, h.from_at, h.to_at, h.library_id, h.folder_id, h.document_id, h.include_audit, h.status, h.created_at, h.created_by, h.released_at";

    /// <summary>The documents a hold covers: the same conditions as <see cref="RecordsSql.Held"/>, for one hold.</summary>
    private const string CoveredBy =
        """
        d.deleted_at is null and (
          h.document_id = d.id
          or (h.library_id = d.library_id and (h.folder_id is null
              or exists (select 1 from documents.folder f where f.id = d.folder_id and h.folder_id = any(f.path))))
          or (h.custodian_id is not null and (
              (d.created_by = h.custodian_id and d.created_at >= coalesce(h.from_at, '-infinity') and d.created_at <= coalesce(h.to_at, 'infinity'))
              or exists (select 1 from documents.document_version hv where hv.document_id = d.id and hv.author_user_id = h.custodian_id
                  and hv.created_at >= coalesce(h.from_at, '-infinity') and hv.created_at <= coalesce(h.to_at, 'infinity')))))
        """;

    private static HoldView ReadHold(Npgsql.NpgsqlDataReader r) => new(r.GetGuid(0), r.GetString(1), r.GetString(2), r.Get<Guid?>("custodian_id"),
        r.Get<DateTimeOffset?>("from_at"), r.Get<DateTimeOffset?>("to_at"), r.Get<Guid?>("library_id"), r.Get<Guid?>("folder_id"), r.Get<Guid?>("document_id"),
        r.GetBoolean(9), r.GetString(10), r.GetFieldValue<DateTimeOffset>(11), r.Get<Guid?>("created_by"), r.Get<DateTimeOffset?>("released_at"), r.GetInt32(14));

    public async Task<IReadOnlyList<HoldView>> HoldsAsync(CancellationToken ct)
    {
        await RequireAsync(Capabilities.RecordsLegalHold, ct);
        return await session.QueryAsync(
            $"""
            select {HoldColumns}, (select count(*)::int from documents.document d where {CoveredBy}) as documents
            from documents.hold h order by h.status, h.created_at desc, h.id
            """, null, ReadHold, ct);
    }

    public async Task<HoldDetail> HoldAsync(Guid id, CancellationToken ct)
    {
        await RequireAsync(Capabilities.RecordsLegalHold, ct);
        var hold = await session.QuerySingleOrDefaultAsync(
            $"select {HoldColumns}, (select count(*)::int from documents.document d where {CoveredBy}) as documents from documents.hold h where h.id = @id",
            new { id }, ReadHold, ct) ?? throw AppException.NotFound();
        var documents = await session.QueryAsync(
            $"select d.id, d.library_id, d.name, d.number from documents.hold h join documents.document d on {CoveredBy} where h.id = @id order by lower(d.name), d.id limit {MaxHeldShown}",
            new { id }, r => new HeldDocument(r.GetGuid(0), r.GetGuid(1), r.GetString(2), r.Get<string>("number")), ct);
        return new HoldDetail(hold, documents);
    }

    /// <summary>
    /// Places a hold. It names exactly one of a custodian (with an optional date range, and optionally their audit
    /// records), a library (optionally one folder of it) or a document, all of the current tenant.
    /// </summary>
    public async Task<HoldDetail> CreateHoldAsync(HoldRequest request, CancellationToken ct)
    {
        await RequireAsync(Capabilities.RecordsLegalHold, ct);
        var errors = new ValidationErrors();
        var name = request.Name?.Trim();
        var reason = request.Reason?.Trim();
        if (string.IsNullOrEmpty(name) || name.Length > MaxHoldNameLength)
        {
            errors.Add("name", "validation.records.hold_name");
        }
        if (string.IsNullOrEmpty(reason) || reason.Length > MaxReasonLength)
        {
            errors.Add("reason", "validation.records.hold_reason");
        }
        var targets = new[] { request.CustodianId is not null, request.LibraryId is not null, request.DocumentId is not null }.Count(t => t);
        if (targets != 1 || (request.FolderId is not null && request.LibraryId is null))
        {
            errors.Add("target", "validation.records.hold_target");
        }
        if (request.From is { } from && request.To is { } to && from > to)
        {
            errors.Add("to", "validation.records.hold_range");
        }
        if ((request.From is not null || request.To is not null || request.IncludeAudit) && request.CustodianId is null)
        {
            errors.Add("custodianId", "validation.records.hold_custodian_only");
        }
        // Every target must be of this tenant: users through the directory, the rest through row-level security.
        if (request.CustodianId is { } custodian && await users.GetAsync(custodian, ct) is null)
        {
            errors.Add("custodianId", "validation.records.hold_custodian");
        }
        if (request.LibraryId is { } library && !await session.ScalarAsync<bool>("select exists (select 1 from documents.library where id = @library)", new { library }, ct))
        {
            errors.Add("libraryId", "validation.records.hold_library");
        }
        if (request.FolderId is { } folder
            && !await session.ScalarAsync<bool>("select exists (select 1 from documents.folder where id = @folder and library_id = @library)", new { folder, library = request.LibraryId }, ct))
        {
            errors.Add("folderId", "validation.records.hold_folder");
        }
        if (request.DocumentId is { } document && !await session.ScalarAsync<bool>("select exists (select 1 from documents.document where id = @document)", new { document }, ct))
        {
            errors.Add("documentId", "validation.records.hold_document");
        }
        errors.ThrowIfAny();
        var id = Ids.New();
        await session.ExecAsync(
            """
            insert into documents.hold (tenant_id, id, name, reason, custodian_id, from_at, to_at, library_id, folder_id, document_id, include_audit, created_at, created_by)
            values (platform.current_tenant_id(), @id, @name, @reason, @custodian, @from, @to, @library, @folder, @document, @audit, @now, @by)
            """, new
            {
                id, name, reason, custodian = request.CustodianId, from = request.From, to = request.To, library = request.LibraryId, folder = request.FolderId,
                document = request.DocumentId, audit = request.IncludeAudit, now = clock.UtcNow, by = context.ActorId,
            }, ct);
        var detail = await HoldAsync(id, ct);
        // The reason stays out of the audit record's changes: it can name people and matters under investigation.
        await audit.WriteAsync(new AuditEntry(AuditActions.RecordsHoldApplied, "legal_hold", id.ToString(),
            Changes: new
            {
                scope = request.CustodianId is not null ? "custodian" : request.LibraryId is not null ? request.FolderId is not null ? "folder" : "library" : "document",
                custodian = request.CustodianId, from = request.From, to = request.To, library = request.LibraryId, folder = request.FolderId, document = request.DocumentId,
                includeAudit = request.IncludeAudit, documents = detail.Hold.Documents,
            }), ct);
        await outbox.EnqueueAsync(new LegalHoldApplied(id), ct);
        return detail;
    }

    public async Task<HoldDetail> ReleaseHoldAsync(Guid id, CancellationToken ct)
    {
        await RequireAsync(Capabilities.RecordsLegalHold, ct);
        if (await session.ExecAsync("update documents.hold set status = 'released', released_at = @now, released_by = @by where id = @id and status = 'active'",
            new { id, now = clock.UtcNow, by = context.ActorId }, ct) == 0)
        {
            throw await session.ScalarAsync<bool>("select exists (select 1 from documents.hold where id = @id)", new { id }, ct)
                ? AppException.Conflict("conflict.records.hold_released") : AppException.NotFound();
        }
        await audit.WriteAsync(new AuditEntry(AuditActions.RecordsHoldReleased, "legal_hold", id.ToString()), ct);
        await outbox.EnqueueAsync(new LegalHoldReleased(id), ct);
        return await HoldAsync(id, ct);
    }

    // Background work for one tenant (the document maintenance worker, as the system actor)

    /// <summary>
    /// Acts on documents whose label period has ended. A delete label deletes them without review, unless they are held
    /// (the first attempt is audited and they wait), checked out, or met a hold before (then they go to review). A review
    /// label puts them in the queue; an archive label archives them. Documents in a release wait for it to end.
    /// </summary>
    public async Task<int> RunAsync(DateTimeOffset now, int limit, CancellationToken ct)
    {
        if (!await entitlements.IsEnabledAsync(Capabilities.RecordsRetention, ct))
        {
            return 0;
        }
        var waiting = "d.deleted_at is null and d.checked_in and d.disposition is null and d.release_instance_id is null";
        var blocked = await session.QueryAsync(
            $"""
            update documents.document d set deletion_blocked_at = @now
            where d.id in (select d.id from documents.document d where {waiting} and d.retention_action = 'delete' and d.deletion_blocked_at is null
              and {RecordsSql.Due("d", "@now")} and {RecordsSql.Held("d")} limit @limit)
            returning d.id, d.library_id
            """, new { now, limit }, r => (Id: r.GetGuid(0), Library: r.GetGuid(1)), ct);
        foreach (var (id, library) in blocked)
        {
            await audit.WriteAsync(new AuditEntry(AuditActions.RecordsDeletionBlocked, Securables.Document, id.ToString(), AuditOutcome.Denied,
                Changes: new { library, path = "retention" }), ct);
        }
        var due = await session.QueryAsync(
            $"""
            select d.id, d.library_id, d.retention_action, d.deletion_blocked_at is not null or d.checked_out_by is not null as review
            from documents.document d
            where {waiting} and {RecordsSql.Due("d", "@now")} and not {RecordsSql.Held("d")}
            order by d.id limit @limit for update of d skip locked
            """, new { now, limit }, r => (Id: r.GetGuid(0), Library: r.GetGuid(1), Action: r.GetString(2), Review: r.GetBoolean(3)), ct);
        foreach (var (id, library, action, review) in due)
        {
            if (action == RetentionLabelActions.Archive)
            {
                await session.ExecAsync("update documents.document set disposition = 'archived', checked_out_by = null, checked_out_at = null where id = @id", new { id }, ct);
                await audit.WriteAsync(new AuditEntry(AuditActions.RecordsArchived, Securables.Document, id.ToString(), Changes: new { library }), ct);
                await outbox.EnqueueAsync(new DocumentWithdrawn(id, library), ct);
            }
            else if (action == RetentionLabelActions.Delete && !review)
            {
                await DisposeAsync(id, "automatic", null, ct);
            }
            else
            {
                await session.ExecAsync("update documents.document set disposition = 'review', disposition_due_at = @now where id = @id", new { id, now }, ct);
            }
        }
        return blocked.Count + due.Count;
    }
}
