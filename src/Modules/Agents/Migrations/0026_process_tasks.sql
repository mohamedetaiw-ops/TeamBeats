-- Spec 027 T023 (R3, FR-024): L4 agent tasks of approved processes. One row per process step run, so a redelivered
-- request runs the agent once. A limit breach opens a security incident by itself: such an incident has a kind and no
-- person who opened it. Tenant-owned, forced RLS.
create table agents.process_task (
  tenant_id uuid not null,
  service_task_id uuid not null,
  instance_id uuid not null,
  definition_key text not null,
  definition_version int not null,
  element_id text not null,
  agent_id uuid not null,
  user_id uuid not null,
  run_id uuid null,
  outcome text not null check (outcome in ('completed', 'failed')),
  reason text null check (length(reason) <= 100),
  created_at timestamptz not null,
  primary key (tenant_id, service_task_id)
);
create index process_task_agent on agents.process_task (tenant_id, agent_id, created_at);
select platform.enable_tenant_rls('agents.process_task');
grant select, insert on agents.process_task to tb_app;

alter table agents.security_incident alter column opened_by drop not null;
alter table agents.security_incident add column kind text null check (kind in ('l4_limit'));

alter table agents.run drop constraint run_mode_check;
alter table agents.run add constraint run_mode_check check (mode in ('run', 'test', 'eval', 'briefing', 'autonomous'));
