-- Spec 014 T008 with spec 034 (R1): runs of the assistant's platform suites. An assistant run tests the knowledge agent
-- on the assistant use case, so it has no agent, version or unit; each case carries the synthetic passages it gives the
-- assistant in place of retrieval.
alter table evaluation.run add column capability text not null default 'agent' check (capability in ('agent', 'assistant'));
alter table evaluation.run alter column agent_id drop not null;
alter table evaluation.run alter column agent_version drop not null;
alter table evaluation.run alter column org_unit_id drop not null;
alter table evaluation.run add constraint run_capability_subject check (
  (capability = 'agent' and agent_id is not null and agent_version is not null and org_unit_id is not null)
  or (capability = 'assistant' and agent_id is null and agent_version is null and org_unit_id is null));
create index run_assistant on evaluation.run (tenant_id, created_at desc) where capability = 'assistant';

alter table evaluation.case_result add column context jsonb null;
