-- Spec 024 T021 (R3, US8): user tasks of an external engine (Camunda 8) shown in the TeamBeats inbox. Only the task
-- reference, its assignee in TeamBeats and the state of the completion sent from TeamBeats are kept; process state
-- stays in the external engine. The outcome and comment are kept only until Camunda has them. Tenant-owned, forced RLS.
create table workflow.external_task (
  tenant_id uuid not null,
  id uuid not null,
  connection_id uuid not null,
  external_key text not null check (length(external_key) <= 20),
  user_id uuid not null,
  name text not null check (length(name) <= 200),
  process_name text null check (length(process_name) <= 200),
  created_at timestamptz not null,
  due_at timestamptz null,
  status text not null check (status in ('open', 'completing', 'completed', 'failed', 'closed')),
  outcome text null check (length(outcome) <= 100),
  comment text null check (length(comment) <= 2000),
  attempts int not null default 0,
  next_attempt_at timestamptz null,
  last_error text null check (length(last_error) <= 100),
  seen_at timestamptz not null,
  completed_at timestamptz null,
  primary key (tenant_id, id),
  unique (tenant_id, connection_id, external_key)
);
create index external_task_user on workflow.external_task (tenant_id, user_id) where status in ('open', 'completing', 'failed');
create index external_task_retry on workflow.external_task (tenant_id, next_attempt_at) where status = 'failed' and next_attempt_at is not null;
select platform.enable_tenant_rls('workflow.external_task');
grant select, insert, update on workflow.external_task to tb_app;
