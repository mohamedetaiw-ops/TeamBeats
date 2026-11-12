-- Spec 022 slice 5 (T022) with spec 019: an approved employee data change written back to HR through the HR REST pack.
-- The values live here only until HR answers: they are cleared when the write is recorded or fails (FR-019).
create table integrations.hr_data_change_handoff (
  tenant_id uuid not null,
  id uuid not null,
  request_id uuid not null,
  user_id uuid not null,
  input jsonb null,
  connection_id uuid null,
  state text not null check (state in ('queued', 'recorded', 'failed')),
  attempt int not null default 0,
  external_reference text null,
  error_class text null,
  next_retry_at timestamptz null,
  created_at timestamptz not null,
  recorded_at timestamptz null,
  primary key (tenant_id, id),
  unique (tenant_id, request_id),
  check (state = 'queued' or input is null)
);
create index hr_data_change_handoff_due on integrations.hr_data_change_handoff (tenant_id, next_retry_at) where state = 'queued';
select platform.enable_tenant_rls('integrations.hr_data_change_handoff');
grant select, insert, update on integrations.hr_data_change_handoff to tb_app;
