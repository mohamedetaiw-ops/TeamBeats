-- Spec 024 (R1): the engine core for approval routes. Definitions are immutable versions; instances, user tasks, jobs
-- and history are tenant-owned. Every table has forced row-level security.
create schema workflow;
grant usage on schema workflow to tb_app;

create table workflow.process_definition (
  tenant_id uuid not null,
  key text not null,
  version int not null,
  name text not null,
  source text not null check (source in ('service_route', 'content_route', 'policy_route', 'document_route')),
  bpmn_xml text not null,
  compiled jsonb not null,
  published_at timestamptz not null default now(),
  published_by uuid null,
  primary key (tenant_id, key, version)
);
select platform.enable_tenant_rls('workflow.process_definition');
grant select, insert on workflow.process_definition to tb_app;

create table workflow.process_instance (
  tenant_id uuid not null,
  id uuid not null,
  definition_key text not null,
  definition_version int not null,
  business_key text not null,
  org_unit_id uuid null,
  started_by uuid null,
  status text not null check (status in ('active', 'completed', 'withdrawn')),
  outcome text null,
  variables jsonb not null default '{}'::jsonb,
  started_at timestamptz not null default now(),
  ended_at timestamptz null,
  primary key (tenant_id, id),
  foreign key (tenant_id, definition_key, definition_version) references workflow.process_definition (tenant_id, key, version)
);
create unique index process_instance_business_key on workflow.process_instance (tenant_id, definition_key, business_key);
select platform.enable_tenant_rls('workflow.process_instance');
grant select, insert, update on workflow.process_instance to tb_app;

create table workflow.user_task (
  tenant_id uuid not null,
  id uuid not null,
  instance_id uuid not null,
  element_id text not null,
  step_index int not null,
  candidates jsonb not null,
  excluded_user_ids uuid[] not null default '{}',
  due_at timestamptz null,
  status text not null check (status in ('open', 'completed', 'cancelled')),
  outcome text null,
  comment text null,
  completed_by uuid null,
  on_behalf_of uuid null,
  created_at timestamptz not null default now(),
  completed_at timestamptz null,
  primary key (tenant_id, id),
  foreign key (tenant_id, instance_id) references workflow.process_instance (tenant_id, id)
);
create index user_task_open on workflow.user_task (tenant_id, created_at) where status = 'open';
create index user_task_instance on workflow.user_task (tenant_id, instance_id);
select platform.enable_tenant_rls('workflow.user_task');
grant select, insert, update on workflow.user_task to tb_app;

-- Timers and continuations (reminders in R1). A job runs at least once; done_at makes a second delivery a no-op.
create table workflow.engine_job (
  tenant_id uuid not null,
  id uuid not null,
  kind text not null,
  instance_id uuid not null,
  task_id uuid null,
  due_at timestamptz not null,
  attempts int not null default 0,
  locked_until timestamptz null,
  done_at timestamptz null,
  primary key (tenant_id, id)
);
create index engine_job_due on workflow.engine_job (tenant_id, due_at) where done_at is null;
select platform.enable_tenant_rls('workflow.engine_job');
grant select, insert, update on workflow.engine_job to tb_app;

create table workflow.instance_history (
  tenant_id uuid not null,
  id bigint generated always as identity,
  instance_id uuid not null,
  element_id text not null,
  event text not null,
  actor_id uuid null,
  on_behalf_of uuid null,
  detail jsonb not null default '{}'::jsonb,
  at timestamptz not null default now(),
  primary key (tenant_id, id)
);
create index instance_history_instance on workflow.instance_history (tenant_id, instance_id, id);
select platform.enable_tenant_rls('workflow.instance_history');
grant select, insert on workflow.instance_history to tb_app;
