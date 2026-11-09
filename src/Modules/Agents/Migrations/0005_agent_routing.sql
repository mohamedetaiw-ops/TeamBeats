-- Specs 020 T012 and 014 T004 (R1): routing from the assistant to an agent and contextual actions. Tenant-owned with
-- forced RLS.

-- A routing decision (020 US7 scenario 3, 014 FR-006): how many agents the person could use, which one answered and
-- how it was chosen. No question text is kept (metadata only, spec 013 logging policy).
create table agents.route_decision (
  tenant_id uuid not null,
  id uuid not null,
  user_id uuid not null,
  method text not null check (method in ('model', 'chosen', 'none', 'knowledge')),
  candidates int not null default 0,
  agent_id uuid null,
  model text null,
  reason text null,
  created_at timestamptz not null,
  primary key (tenant_id, id)
);
create index route_decision_user on agents.route_decision (tenant_id, user_id, created_at desc);
select platform.enable_tenant_rls('agents.route_decision');
grant select, insert, delete on agents.route_decision to tb_app;

-- A run started by routing or by a contextual action keeps the decision and the item it worked on.
alter table agents.run add column route_id uuid null;
alter table agents.run add column item_source text null;
alter table agents.run add column item_id uuid null;

-- An assistant turn answered by an agent names its run, and may wait for the person to confirm an action.
alter table agents.turn add column run_id uuid null;
alter table agents.turn drop constraint turn_outcome_check;
alter table agents.turn add constraint turn_outcome_check
  check (outcome in ('answered', 'no_evidence', 'policy_blocked', 'unavailable', 'action_pending'));
