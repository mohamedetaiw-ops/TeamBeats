-- Spec 034 R2 T022 (US9 scenario 1, FR-027): business outcome baselines. Per agent, the task it does and how many
-- minutes that task takes a person without AI (for example 12 minutes to raise a VPN request), set by the AI Policy
-- Administrator. Outcomes themselves are computed from the agent runs on demand and never stored per person.
create table evaluation.outcome_baseline (
  tenant_id uuid not null,
  agent_id uuid not null,
  task_names jsonb not null,
  minutes numeric(6, 1) not null check (minutes > 0 and minutes <= 480),
  updated_by uuid not null,
  updated_at timestamptz not null,
  primary key (tenant_id, agent_id)
);
select platform.enable_tenant_rls('evaluation.outcome_baseline');
grant select, insert, update, delete on evaluation.outcome_baseline to tb_app;
