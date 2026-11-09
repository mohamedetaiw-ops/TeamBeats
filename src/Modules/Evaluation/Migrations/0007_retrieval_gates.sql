-- Spec 034 US5 scenario 5 with spec 012 FR-008 (R1): release gates for proposed retrieval configurations. The gate is
-- one run of the retrieval cases of the published agents (their own dataset cases that name expected or forbidden
-- sources), each as its persona, against the shadow index of the change; the retrieval gate records the decision.
alter table evaluation.run drop constraint run_trigger_check;
alter table evaluation.run add constraint run_trigger_check check (trigger in ('manual', 'gate', 'route', 'retrieval'));
alter table evaluation.run drop constraint run_capability_check;
alter table evaluation.run add constraint run_capability_check check (capability in ('agent', 'assistant', 'retrieval'));
alter table evaluation.run drop constraint run_capability_subject;
alter table evaluation.run add constraint run_capability_subject check (
  (capability = 'agent' and agent_id is not null and agent_version is not null and org_unit_id is not null)
  or (capability in ('assistant', 'retrieval') and agent_id is null and agent_version is null and org_unit_id is null));
alter table evaluation.run add column retrieval_change_id uuid null;
alter table evaluation.run add constraint run_retrieval_change check ((capability = 'retrieval') = (retrieval_change_id is not null));
create index run_retrieval_change on evaluation.run (tenant_id, retrieval_change_id) where retrieval_change_id is not null;

create table evaluation.retrieval_gate (
  tenant_id uuid not null,
  change_id uuid not null,
  config_version int not null,
  status text not null check (status in ('running', 'decided')),
  result text null check (result in ('passed', 'failed', 'incomplete', 'failed_critical')),
  failures jsonb not null default '[]',
  run_id uuid null,
  created_at timestamptz not null,
  decided_at timestamptz null,
  primary key (tenant_id, change_id)
);
select platform.enable_tenant_rls('evaluation.retrieval_gate');
grant select, insert, update on evaluation.retrieval_gate to tb_app;
