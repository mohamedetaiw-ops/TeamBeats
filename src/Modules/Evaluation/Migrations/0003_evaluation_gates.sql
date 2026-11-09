-- Spec 034 slice 3 (R1): overrides of blocking gate failures (US5 scenario 3, FR-014). One per run; critical failures
-- and High risk versions are never overridden, which the service checks. The agent owner gets a follow-up task that
-- stays open until a later version of the agent passes its gate.
create table evaluation.gate_override (
  tenant_id uuid not null,
  run_id uuid not null,
  agent_id uuid not null,
  agent_version int not null,
  reason text not null,
  overridden_by uuid not null,
  overridden_at timestamptz not null,
  owner_id uuid not null,
  follow_up_done_at timestamptz,
  primary key (tenant_id, run_id),
  foreign key (tenant_id, run_id) references evaluation.run (tenant_id, id) on delete cascade
);
select platform.enable_tenant_rls('evaluation.gate_override');
grant select, insert, update, delete on evaluation.gate_override to tb_app;
create index gate_override_owner_ix on evaluation.gate_override (tenant_id, owner_id) where follow_up_done_at is null;
