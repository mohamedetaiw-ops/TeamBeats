-- Specs 020 T005 and 027 T001, T005, T006, T010 (R1): agent runs, their ordered steps (the execution record) and the
-- checkpoint a run waits on for a confirmation. Every table is tenant-owned with forced RLS. Run records hold metadata
-- only (spec 013 logging policy): the conversation text lives in the checkpoint while a run waits and is removed when
-- it ends.

create table agents.run (
  tenant_id uuid not null,
  id uuid not null,
  agent_id uuid not null,
  agent_version int not null,
  user_id uuid not null,
  org_unit_id uuid not null,
  mode text not null check (mode in ('run', 'test')),
  status text not null check (status in ('running', 'waiting', 'completed', 'stopped', 'failed')),
  outcome text null,
  reason text null,
  language text not null,
  steps int not null default 0,
  tool_calls int not null default 0,
  denials int not null default 0,
  model text null,
  provider text null,
  input_tokens int not null default 0,
  output_tokens int not null default 0,
  classification text not null,
  sources jsonb not null default '[]',
  created_at timestamptz not null,
  updated_at timestamptz not null,
  finished_at timestamptz null,
  primary key (tenant_id, id),
  foreign key (tenant_id, agent_id) references agents.agent (tenant_id, id)
);
create index run_agent on agents.run (tenant_id, agent_id, created_at desc);
create index run_user on agents.run (tenant_id, user_id, created_at desc);
select platform.enable_tenant_rls('agents.run');
grant select, insert, update, delete on agents.run to tb_app;

-- The ordered steps of a run (027 FR-016): model turns, tool calls with the gateway decision, confirmations and denials.
-- Parameters are masked when the run carries Confidential or Restricted data.
create table agents.run_step (
  tenant_id uuid not null,
  run_id uuid not null,
  n int not null,
  kind text not null check (kind in ('model', 'tool', 'confirmation')),
  tool text null,
  decision text not null,
  reason text null,
  parameter_hash text null,
  parameters jsonb null,
  classification text null,
  duration_ms int not null default 0,
  at timestamptz not null,
  primary key (tenant_id, run_id, n),
  foreign key (tenant_id, run_id) references agents.run (tenant_id, id) on delete cascade
);
select platform.enable_tenant_rls('agents.run_step');
grant select, insert, update, delete on agents.run_step to tb_app;

-- Where a waiting run stands (020 plan: run_checkpoint): the conversation so far and the pending call bound by its hash.
create table agents.run_checkpoint (
  tenant_id uuid not null,
  run_id uuid not null,
  state jsonb not null,
  updated_at timestamptz not null,
  primary key (tenant_id, run_id),
  foreign key (tenant_id, run_id) references agents.run (tenant_id, id) on delete cascade
);
select platform.enable_tenant_rls('agents.run_checkpoint');
grant select, insert, update, delete on agents.run_checkpoint to tb_app;
