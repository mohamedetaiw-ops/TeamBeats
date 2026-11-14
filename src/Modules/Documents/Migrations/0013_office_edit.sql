-- Spec 011 R2 FR-010: Office editing in the browser. While a contributor edits a checked-out document in Office for
-- the web, the copy lives in their own OneDrive; this row says where, so check-in can read it back and remove it.
create table documents.office_edit (
  tenant_id uuid not null,
  document_id uuid not null,
  user_id uuid not null,
  item_id text not null,
  edit_url text not null,
  version_id uuid not null,
  started_at timestamptz not null,
  primary key (tenant_id, document_id),
  foreign key (tenant_id, document_id) references documents.document (tenant_id, id)
);
select platform.enable_tenant_rls('documents.office_edit');
grant select, insert, delete on documents.office_edit to tb_app;
