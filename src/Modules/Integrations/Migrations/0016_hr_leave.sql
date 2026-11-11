-- Spec 019 US7 and spec 022 FR-006: HR as the leave balance source. The setting is versioned configuration; the last
-- balances read from HR are kept for when HR does not answer; approved leave, cancellations and changes are handed to
-- HR once each and retried until HR records them. Tenant-owned with forced row-level security.
create table integrations.hr_leave_setting (
  tenant_id uuid not null primary key,
  source text not null default 'teambeats' check (source in ('teambeats', 'hr')),
  allow_stale_submit boolean not null default false,
  version int not null default 1,
  updated_at timestamptz not null,
  updated_by uuid null
);
select platform.enable_tenant_rls('integrations.hr_leave_setting');
grant select, insert, update, delete on integrations.hr_leave_setting to tb_app;

create table integrations.hr_leave_balance (
  tenant_id uuid not null,
  user_id uuid not null,
  type_key text not null,
  balance numeric(8,2) not null,
  read_at timestamptz not null,
  primary key (tenant_id, user_id, type_key)
);
select platform.enable_tenant_rls('integrations.hr_leave_balance');
grant select, insert, update, delete on integrations.hr_leave_balance to tb_app;

create table integrations.hr_leave_handoff (
  tenant_id uuid not null,
  id uuid not null,
  request_id uuid not null,
  change int not null,
  kind text not null check (kind in ('approved', 'cancelled', 'changed')),
  user_id uuid not null,
  input jsonb not null,
  connection_id uuid null,
  state text not null check (state in ('queued', 'recorded', 'failed')),
  attempt int not null default 0,
  external_reference text null,
  error_class text null,
  next_retry_at timestamptz null,
  created_at timestamptz not null,
  recorded_at timestamptz null,
  primary key (tenant_id, id),
  unique (tenant_id, request_id, change)
);
create index hr_leave_handoff_due on integrations.hr_leave_handoff (tenant_id, next_retry_at) where state = 'queued';
select platform.enable_tenant_rls('integrations.hr_leave_handoff');
grant select, insert, update, delete on integrations.hr_leave_handoff to tb_app;
