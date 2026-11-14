using TeamBeats.Persistence;

namespace TeamBeats.Modules.Documents;

/// <summary>
/// The SQL that every deletion path of documents shares (spec 011 US6, US7): whether a document is held, when its label's
/// period ends, and the labels it inherits from its library and type. Kept in one place so that the recycle bin purge,
/// user deletion and disposition decide the same way.
/// </summary>
internal static class RecordsSql
{
    /// <summary>
    /// True when an active legal hold covers the document: the document itself, its library or a folder above it, its
    /// custodian as creator or version author in the hold's range, or a hold placed on it through the contract.
    /// </summary>
    public static string Held(string d) =>
        $"""
        (exists (select 1 from documents.hold h where h.status = 'active' and (
            h.document_id = {d}.id
            or (h.library_id = {d}.library_id and (h.folder_id is null
                or exists (select 1 from documents.folder f where f.id = {d}.folder_id and h.folder_id = any(f.path))))
            or (h.custodian_id is not null and (
                ({d}.created_by = h.custodian_id and {d}.created_at >= coalesce(h.from_at, '-infinity') and {d}.created_at <= coalesce(h.to_at, 'infinity'))
                or exists (select 1 from documents.document_version hv where hv.document_id = {d}.id and hv.author_user_id = h.custodian_id
                    and hv.created_at >= coalesce(h.from_at, '-infinity') and hv.created_at <= coalesce(h.to_at, 'infinity'))))))
          or exists (select 1 from documents.legal_hold lh where lh.resource_type = 'doc_document' and lh.resource_id = {d}.id))
        """;

    /// <summary>When the label's period starts for the document, or null while its trigger has not happened.</summary>
    public static string TriggerAt(string d) =>
        $"""
        (select case l.trigger
            when 'created' then {d}.created_at
            when 'modified' then {d}.updated_at
            when 'effective' then (select tv.effective_from from documents.document_version tv where tv.id = {d}.effective_version_id)
            when 'superseded' then {d}.withdrawn_at
            else documents.try_date({d}.metadata ->> l.trigger_field) end
          from documents.retention_label l where l.id = {d}.retention_label_id)
        """;

    /// <summary>When the document's retention period ends, or null while its trigger has not happened.</summary>
    public static string EndsAt(string d) => $"({TriggerAt(d)} + make_interval(months => {d}.retention_months))";

    /// <summary>True while a label keeps the document: its period has not ended, or its trigger has not happened yet.</summary>
    public static string InPeriod(string d, string now) => $"({d}.retention_label_id is not null and coalesce({EndsAt(d)} > {now}, true))";

    /// <summary>True when a label's period has ended for the document.</summary>
    public static string Due(string d, string now) => $"({d}.retention_label_id is not null and {EndsAt(d)} <= {now})";

    /// <summary>
    /// Gives documents the label of their library or type, the longer one when both have one (section 6), unless a records
    /// manager labelled them by hand. A document already carrying the label keeps its period; one that loses its inherited
    /// label is unlabelled. Narrowed to one library, one type or some documents.
    /// </summary>
    public static async Task InheritAsync(IDbSession session, DateTimeOffset now, CancellationToken ct, Guid? library = null, Guid? type = null, Guid[]? ids = null)
    {
        const string Scope = "(@library::uuid is null or d.library_id = @library) and (@type::uuid is null or d.type_id = @type) and (@ids::uuid[] is null or d.id = any(@ids))";
        await session.ExecAsync(
            $"""
            update documents.document d set retention_label_id = pick.label, retention_source = pick.source, retention_months = pick.months,
              retention_action = pick.action, labelled_at = @now, disposition = null, disposition_due_at = null
            from (
              select distinct on (d.id) d.id as doc, l.id as label, s.source, l.period_months as months, l.action
              from documents.document d
              join documents.library lib on lib.id = d.library_id
              left join documents.document_type t on t.id = d.type_id
              cross join lateral (values ('library', lib.retention_label_id), ('type', t.retention_label_id)) s(source, label)
              join documents.retention_label l on l.id = s.label and l.status = 'active'
              where {Scope} and d.retention_source is distinct from 'manual' and d.deleted_at is null
              order by d.id, l.period_months desc, s.source desc
            ) pick
            where d.id = pick.doc and d.retention_label_id is distinct from pick.label;

            update documents.document d set retention_label_id = null, retention_source = null, retention_months = null, retention_action = null,
              labelled_at = null, disposition = null, disposition_due_at = null
            where {Scope} and d.retention_source in ('library', 'type') and d.deleted_at is null
              and not exists (
                select 1 from documents.library lib
                left join documents.document_type t on t.id = d.type_id
                join documents.retention_label l on l.status = 'active' and l.id in (lib.retention_label_id, t.retention_label_id)
                where lib.id = d.library_id);
            """, new { library, type, ids, now }, ct);
    }
}
