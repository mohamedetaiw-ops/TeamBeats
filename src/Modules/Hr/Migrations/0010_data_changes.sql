-- Spec 022 slice 5 (T022): employee data change requests (US6, FR-019, FR-020). The new values are kept only until
-- the change is written to HR, refused or withdrawn; then only the names of the changed fields remain.
create table hr.data_change_settings (
  tenant_id uuid not null primary key,
  -- Per group: {"bank": {"enabled": true, "document": true}, ...}; groups not listed take the defaults.
  groups jsonb not null,
  version int not null default 1,
  updated_by uuid null,
  updated_at timestamptz null
);
select platform.enable_tenant_rls('hr.data_change_settings');
grant select, insert, update on hr.data_change_settings to tb_app;

create table hr.data_change_document (
  tenant_id uuid not null,
  id uuid not null,
  uploaded_by uuid not null,
  file_name text not null,
  size_bytes bigint not null,
  content_type text not null,
  scan_state text not null check (scan_state in ('clean', 'failed')),
  request_id uuid null,
  -- A sensitive group's document (a bank letter) is deleted with the values once the change is written.
  deleted_at timestamptz null,
  created_at timestamptz not null,
  primary key (tenant_id, id)
);
select platform.enable_tenant_rls('hr.data_change_document');
grant select, insert, update on hr.data_change_document to tb_app;

create table hr.data_change_request (
  tenant_id uuid not null,
  id uuid not null,
  user_id uuid not null,
  org_unit_id uuid null,
  group_key text not null check (group_key in ('address', 'emergency_contact', 'bank', 'marital_status', 'dependants')),
  field_keys text[] not null,
  -- The new values, plain text; null once written, refused or withdrawn (FR-019).
  values jsonb null,
  reason text null check (reason is null or char_length(reason) between 1 and 500),
  document_id uuid null,
  status text not null check (status in ('pending', 'approved', 'completed', 'write_failed', 'rejected', 'withdrawn')),
  write_back text null check (write_back in ('hr', 'manual')),
  external_reference text null,
  instance_id uuid null,
  decided_by uuid null,
  decided_at timestamptz null,
  completed_at timestamptz null,
  created_at timestamptz not null,
  updated_at timestamptz not null,
  primary key (tenant_id, id),
  check (status in ('pending', 'approved') or values is null)
);
create unique index data_change_open on hr.data_change_request (tenant_id, user_id, group_key) where status in ('pending', 'approved');
create index data_change_user on hr.data_change_request (tenant_id, user_id, created_at desc);
select platform.enable_tenant_rls('hr.data_change_request');
grant select, insert, update on hr.data_change_request to tb_app;
