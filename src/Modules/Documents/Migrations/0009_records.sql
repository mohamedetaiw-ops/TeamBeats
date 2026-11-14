-- Spec 011 R2 US6 and US7: retention labels, disposition review and legal holds on TeamBeats libraries.

-- A retention label (FR-013): how long after its trigger a document is kept, and what happens then. A field trigger
-- names a date field of the document's type.
create table documents.retention_label (
  tenant_id uuid not null,
  id uuid not null,
  names jsonb not null,
  period_months int not null check (period_months between 1 and 1200),
  trigger text not null check (trigger in ('created', 'modified', 'effective', 'superseded', 'field')),
  trigger_field text null check (trigger_field is null or length(trigger_field) between 1 and 64),
  action text not null check (action in ('delete', 'review', 'archive')),
  status text not null default 'active' check (status in ('active', 'retired')),
  created_at timestamptz not null,
  created_by uuid null,
  updated_at timestamptz not null,
  row_version int not null default 1,
  primary key (tenant_id, id),
  check ((trigger = 'field') = (trigger_field is not null))
);
select platform.enable_tenant_rls('documents.retention_label');
grant select, insert, update on documents.retention_label to tb_app;

-- Labels applied by library or document type; a document labelled by hand keeps its own.
alter table documents.library add column retention_label_id uuid null;
alter table documents.document_type add column retention_label_id uuid null;

-- The label a document carries, how it came to carry it, and the period and action in force for it: a label changed
-- to a shorter period reaches documents labelled before only when the records manager reapplies it (US6 scenario 5).
-- disposition is 'review' while the document waits in the queue and 'archived' once a label archived it.
-- deletion_blocked_at is set the first time a background deletion met a legal hold, so the attempt is audited once and
-- the document goes to review instead of being deleted when the hold is released (US7 scenario 3).
alter table documents.document add column retention_label_id uuid null;
alter table documents.document add column retention_source text null check (retention_source in ('library', 'type', 'manual'));
alter table documents.document add column retention_months int null;
alter table documents.document add column retention_action text null check (retention_action in ('delete', 'review', 'archive'));
alter table documents.document add column labelled_at timestamptz null;
alter table documents.document add column disposition text null check (disposition in ('review', 'archived'));
alter table documents.document add column disposition_due_at timestamptz null;
alter table documents.document add column deletion_blocked_at timestamptz null;
create index document_retention on documents.document (tenant_id, retention_label_id) where retention_label_id is not null and deleted_at is null;
create index document_disposition on documents.document (tenant_id, disposition_due_at) where disposition = 'review';

-- What is left of a disposed document (FR-014): its number, label, dates and approver, never its name or content.
create table documents.disposition_record (
  tenant_id uuid not null,
  id uuid not null,
  document_id uuid not null,
  library_id uuid not null,
  number text null,
  label_id uuid null,
  label_names jsonb not null,
  versions int not null,
  mode text not null check (mode in ('reviewed', 'automatic')),
  approved_by uuid null,
  disposed_at timestamptz not null,
  primary key (tenant_id, id)
);
create index disposition_record_at on documents.disposition_record (tenant_id, disposed_at desc);
select platform.enable_tenant_rls('documents.disposition_record');
grant select, insert on documents.disposition_record to tb_app;

-- A legal hold (FR-015) selects documents by custodian and date range, by library or folder, or one document, and
-- may also keep the audit records of its custodian. Released holds are kept for the record.
create table documents.hold (
  tenant_id uuid not null,
  id uuid not null,
  name text not null check (length(name) between 1 and 200),
  reason text not null check (length(reason) between 1 and 2000),
  custodian_id uuid null,
  from_at timestamptz null,
  to_at timestamptz null,
  library_id uuid null,
  folder_id uuid null,
  document_id uuid null,
  include_audit boolean not null default false,
  status text not null default 'active' check (status in ('active', 'released')),
  created_at timestamptz not null,
  created_by uuid null,
  released_at timestamptz null,
  released_by uuid null,
  primary key (tenant_id, id),
  check (num_nonnulls(custodian_id, library_id, document_id) = 1),
  check (folder_id is null or library_id is not null),
  check (from_at is null or to_at is null or from_at <= to_at),
  check (not include_audit or custodian_id is not null)
);
create index hold_active on documents.hold (tenant_id) where status = 'active';
select platform.enable_tenant_rls('documents.hold');
grant select, insert, update on documents.hold to tb_app;

-- A date metadata value as a time, or null when the value is not a date (a custom date field trigger).
create function documents.try_date(value text) returns timestamptz
language plpgsql immutable
as $$
begin
  return (value::date)::timestamp at time zone 'UTC';
exception when others then
  return null;
end;
$$;
grant execute on function documents.try_date(text) to tb_app;
