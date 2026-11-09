-- Spec 011 (R1) US3, FR-007, FR-008: controlled documents. Numbers come from a counter per numbering scheme (the type's
-- prefix) that never goes back, so a number is never reused, even after a purge. A release runs on the process engine;
-- an approved version waits as scheduled until its effective date. Review dates open the owner's review task. Read
-- confirmations are kept per person and major version. A controlled copy is the PDF with its control footer.
create table documents.number_scheme (
  tenant_id uuid not null,
  prefix text not null,
  last_value bigint not null,
  primary key (tenant_id, prefix)
);
select platform.enable_tenant_rls('documents.number_scheme');
grant select, insert, update on documents.number_scheme to tb_app;

create unique index document_number on documents.document (tenant_id, number) where number is not null;

alter table documents.document add column release_instance_id uuid null;
alter table documents.document add column release_version_id uuid null;
alter table documents.document add column release_effective_from timestamptz null;
alter table documents.document add column release_submitted_by uuid null;
alter table documents.document add column scheduled_version_id uuid null;
alter table documents.document add column review_instance_id uuid null;
alter table documents.document add column review_overdue_notified_at timestamptz null;
alter table documents.document add column withdrawn_at timestamptz null;
create index document_scheduled on documents.document (tenant_id) where scheduled_version_id is not null;
create index document_review_due on documents.document (tenant_id, review_due_at) where review_due_at is not null;

alter table documents.document_version add column controlled_key text null;
alter table documents.download_token drop constraint download_token_purpose_check;
alter table documents.download_token add constraint download_token_purpose_check check (purpose in ('download', 'preview', 'controlled_copy'));

create table documents.read_confirmation (
  tenant_id uuid not null,
  document_id uuid not null,
  version_id uuid not null,
  user_id uuid not null,
  requested_at timestamptz not null,
  confirmed_at timestamptz null,
  primary key (tenant_id, version_id, user_id)
);
create index read_confirmation_document on documents.read_confirmation (tenant_id, document_id, user_id);
select platform.enable_tenant_rls('documents.read_confirmation');
grant select, insert, update, delete on documents.read_confirmation to tb_app;
