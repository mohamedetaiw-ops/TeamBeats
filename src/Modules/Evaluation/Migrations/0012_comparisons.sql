-- Spec 034 R2 T021 (US9 scenarios 2 and 3, FR-026): online A/B and champion and challenger comparisons. A comparison
-- of an agent pits the version in force (the champion) against another version or the same version on another model
-- (the challenger) for a share of the people who use it, each person always on the same arm. It needs the AI Policy
-- Administrator's approval and a challenger that passed its gate; stopping it returns everyone to the champion. Both
-- arms are measured by production evaluation with the same metrics, so samples record the comparison and arm.
create table evaluation.comparison (
  tenant_id uuid not null,
  id uuid not null,
  agent_id uuid not null,
  org_unit_id uuid not null,
  kind text not null check (kind in ('version', 'model')),
  champion_version int not null,
  challenger_version int not null,
  challenger_model text null,
  share_percent numeric(5, 2) not null check (share_percent >= 1 and share_percent <= 50),
  -- A model challenger's gate: the champion version evaluated on the challenger model.
  gate_run_id uuid null,
  purpose text null check (purpose is null or length(purpose) <= 500),
  status text not null check (status in ('proposed', 'running', 'stopped', 'rejected')),
  proposed_by uuid not null,
  proposed_at timestamptz not null,
  decided_by uuid null,
  decided_at timestamptz null,
  decision_note text null check (decision_note is null or length(decision_note) <= 500),
  started_at timestamptz null,
  stopped_by uuid null,
  stopped_at timestamptz null,
  stop_reason text null check (stop_reason is null or stop_reason in ('stopped', 'withdrawn', 'champion_changed')),
  primary key (tenant_id, id)
);
create unique index comparison_active on evaluation.comparison (tenant_id, agent_id) where status in ('proposed', 'running');
create index comparison_agent on evaluation.comparison (tenant_id, agent_id, proposed_at desc);
select platform.enable_tenant_rls('evaluation.comparison');
grant select, insert, update on evaluation.comparison to tb_app;

alter table evaluation.production_sample add column comparison_id uuid null;
alter table evaluation.production_sample add column arm text null check (arm is null or arm in ('champion', 'challenger'));
create index production_sample_comparison on evaluation.production_sample (tenant_id, comparison_id) where comparison_id is not null;

alter table evaluation.run add column model text null;
alter table evaluation.run drop constraint run_trigger_check;
alter table evaluation.run add constraint run_trigger_check check (trigger in ('manual', 'gate', 'route', 'retrieval', 'comparison'));
