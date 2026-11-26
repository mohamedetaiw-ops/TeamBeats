-- Spec 034 R3: end to end evaluation of L4 agent tasks of approved processes. One row per step run with its
-- deterministic trajectory metrics (no content), and one row per process instance that had an agent task, closed with
-- the instance outcome. A step names the agent and its organization unit, never the person.
create table evaluation.process_step_eval (
  tenant_id uuid not null,
  service_task_id uuid not null,
  instance_id uuid not null,
  definition_key text not null,
  definition_version int not null,
  element_id text not null,
  agent_id uuid not null,
  agent_version int null,
  org_unit_id uuid not null,
  agent_run_id uuid null,
  outcome text not null check (outcome in ('completed', 'failed')),
  reason text null,
  tool_calls int not null,
  refused int not null,
  out_of_scope int not null,
  needs_approval int not null,
  duration_ms int not null,
  max_minutes int not null,
  metrics jsonb not null,
  passed boolean not null,
  critical boolean not null,
  recorded_at timestamptz not null,
  primary key (tenant_id, service_task_id)
);
create index process_step_eval_definition on evaluation.process_step_eval (tenant_id, definition_key, definition_version, recorded_at desc);
create index process_step_eval_instance on evaluation.process_step_eval (tenant_id, instance_id);
select platform.enable_tenant_rls('evaluation.process_step_eval');
grant select, insert on evaluation.process_step_eval to tb_app;

create table evaluation.process_instance_eval (
  tenant_id uuid not null,
  instance_id uuid not null,
  definition_key text not null,
  definition_version int not null,
  outcome text null,
  closed_at timestamptz null,
  primary key (tenant_id, instance_id)
);
select platform.enable_tenant_rls('evaluation.process_instance_eval');
grant select, insert, update on evaluation.process_instance_eval to tb_app;
