-- Spec 024 T009, T011 and T013 (R2): process models, graph definitions and their running state. A process model is a
-- draft per key that a process owner edits; publishing compiles it into the next immutable version of the definition
-- 'process:<key>' with kind 'graph'. A graph instance keeps its tokens and scopes in state. Every table is tenant-owned
-- with forced row-level security.
create table workflow.process_model (
  tenant_id uuid not null,
  key text not null,
  owner_org_unit_id uuid null,
  names jsonb not null default '{}'::jsonb,
  bpmn_xml text not null check (length(bpmn_xml) <= 2097152),
  problems jsonb not null default '[]'::jsonb,
  created_at timestamptz not null default now(),
  created_by uuid null,
  updated_at timestamptz not null default now(),
  updated_by uuid null,
  primary key (tenant_id, key)
);
select platform.enable_tenant_rls('workflow.process_model');
grant select, insert, update on workflow.process_model to tb_app;

alter table workflow.process_definition add column kind text not null default 'route' check (kind in ('route', 'graph'));
alter table workflow.process_definition add column owner_org_unit_id uuid;
alter table workflow.process_definition add column names jsonb not null default '{}'::jsonb;
alter table workflow.process_definition drop constraint process_definition_source_check;
alter table workflow.process_definition add constraint process_definition_source_check
  check (source in ('service_route', 'content_route', 'policy_route', 'document_route', 'leave_route', 'journey_route', 'attendance_route', 'letter_route',
    'data_change_route', 'expense_route', 'booking_route', 'process_model'));

-- Tokens, scopes and visit counts of a graph instance; a child instance of a call activity knows its parent token.
alter table workflow.process_instance add column state jsonb;
alter table workflow.process_instance add column parent_instance_id uuid;
alter table workflow.process_instance add column parent_token_id uuid;
alter table workflow.process_instance add column depth int not null default 0;
create index process_instance_parent on workflow.process_instance (tenant_id, parent_instance_id) where parent_instance_id is not null;
create index process_instance_definition on workflow.process_instance (tenant_id, definition_key, definition_version, status);

alter table workflow.user_task add column token_id uuid;
alter table workflow.service_task add column token_id uuid;
drop index workflow.service_task_step;
create unique index service_task_step on workflow.service_task (tenant_id, instance_id, element_id) where token_id is null;
create index service_task_token on workflow.service_task (tenant_id, instance_id, token_id) where token_id is not null;

-- An incident of a graph instance may come from a step that is not a service task (an expression, a loop limit, a
-- decision): it names the token instead.
alter table workflow.incident alter column service_task_id drop not null;
alter table workflow.incident add column token_id uuid;
create unique index incident_token_open on workflow.incident (tenant_id, token_id) where status = 'open' and token_id is not null;

-- Timers of graph instances (kind 'timer') and timer start events of a definition (kind 'timer_start', no instance).
alter table workflow.engine_job alter column instance_id drop not null;
alter table workflow.engine_job add column token_id uuid;
alter table workflow.engine_job add column element_id text;
alter table workflow.engine_job add column definition_key text;
alter table workflow.engine_job add column detail jsonb;
create index engine_job_token on workflow.engine_job (tenant_id, token_id) where done_at is null and token_id is not null;

-- A token waiting for a message, or a message start event of a definition key (no instance).
create table workflow.message_subscription (
  tenant_id uuid not null,
  id uuid not null,
  name text not null,
  correlation_key text null,
  instance_id uuid null,
  token_id uuid null,
  element_id text not null,
  definition_key text null,
  created_at timestamptz not null default now(),
  primary key (tenant_id, id),
  check ((instance_id is null) = (definition_key is not null))
);
create index message_subscription_match on workflow.message_subscription (tenant_id, name, correlation_key);
create index message_subscription_instance on workflow.message_subscription (tenant_id, instance_id) where instance_id is not null;
create unique index message_subscription_start on workflow.message_subscription (tenant_id, definition_key, element_id) where definition_key is not null;
select platform.enable_tenant_rls('workflow.message_subscription');
grant select, insert, delete on workflow.message_subscription to tb_app;

-- Every message received, once per message ID. One that matched nothing waits until it expires.
create table workflow.message_buffer (
  tenant_id uuid not null,
  id uuid not null,
  message_id text not null check (length(message_id) between 1 and 200),
  name text not null,
  correlation_key text null,
  payload jsonb not null default '{}'::jsonb,
  source text not null check (source in ('event', 'connector', 'webhook', 'process')),
  received_at timestamptz not null default now(),
  expires_at timestamptz not null,
  correlated_at timestamptz null,
  instance_id uuid null,
  primary key (tenant_id, id)
);
create unique index message_buffer_id on workflow.message_buffer (tenant_id, message_id);
create index message_buffer_waiting on workflow.message_buffer (tenant_id, name, correlation_key, received_at) where correlated_at is null;
select platform.enable_tenant_rls('workflow.message_buffer');
grant select, insert, update, delete on workflow.message_buffer to tb_app;
