-- Spec 027 T015 (R2, US1 L3, US4): an action whose rule is "approval" waits for an approver in the spec 016 inbox. The
-- approval keeps the exact parameters and their hash, who asked, the agent and its reasoning summary; the run waits
-- on it. A worker applies the decision as the person, or cancels the action when nobody decided by the due time.
create table agents.run_approval (
  tenant_id uuid not null,
  id uuid not null,
  run_id uuid not null,
  agent_id uuid not null,
  agent_version int not null,
  requested_by uuid not null,
  tool text not null,
  parameter_hash text not null,
  parameters jsonb not null,
  agent_names jsonb not null,
  summary text null,
  instance_id uuid null,
  status text not null default 'pending' check (status in ('pending', 'approved', 'rejected', 'expired', 'cancelled')),
  decided_by uuid null,
  decided_at timestamptz null,
  comment text null,
  applied_at timestamptz null,
  due_at timestamptz not null,
  created_at timestamptz not null,
  primary key (tenant_id, id),
  foreign key (tenant_id, run_id) references agents.run (tenant_id, id) on delete cascade
);
create index run_approval_run on agents.run_approval (tenant_id, run_id);
create index run_approval_open on agents.run_approval (tenant_id, due_at) where status = 'pending';
create index run_approval_decided on agents.run_approval (tenant_id, decided_at) where status in ('approved', 'rejected') and applied_at is null;
select platform.enable_tenant_rls('agents.run_approval');
grant select, insert, update, delete on agents.run_approval to tb_app;
