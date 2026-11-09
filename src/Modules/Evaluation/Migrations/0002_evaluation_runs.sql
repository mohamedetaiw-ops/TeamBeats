-- Spec 034 slice 2 (R1): runs, case results with their evidence, and the default persona of a unit.

-- Platform cases and cases without a persona run as an employee of the agent's unit with no extra roles.
alter table evaluation.persona add column is_default boolean not null default false;
create unique index persona_default_unit on evaluation.persona (tenant_id, org_unit_id) where is_default;

-- A run of an agent version against its suites (US3). suites holds what was tested: each suite's key, version and
-- metrics, and the dataset versions used, so a result is valid only for exactly that combination (section 6).
create table evaluation.run (
  tenant_id uuid not null,
  id uuid not null,
  agent_id uuid not null,
  agent_version int not null,
  org_unit_id uuid not null,
  trigger text not null check (trigger in ('manual', 'gate')),
  status text not null check (status in ('queued', 'running', 'completed', 'cancelled')),
  suites jsonb not null,
  total int not null,
  done int not null default 0,
  errors int not null default 0,
  verdict text null check (verdict in ('passed', 'failed', 'incomplete')),
  result jsonb null,
  requested_by uuid null,
  cancelled_by uuid null,
  created_at timestamptz not null,
  started_at timestamptz null,
  finished_at timestamptz null,
  primary key (tenant_id, id)
);
create index run_agent on evaluation.run (tenant_id, agent_id, created_at desc);
select platform.enable_tenant_rls('evaluation.run');
grant select, insert, update, delete on evaluation.run to tb_app;

-- One case of a run with what it produced: the execution record it points to, the answer, each metric's value and
-- the judge's verdicts. Content is synthetic dataset content (section 11).
create table evaluation.case_result (
  tenant_id uuid not null,
  run_id uuid not null,
  n int not null,
  suite_key text not null,
  dataset_id uuid null,
  dataset_version int null,
  case_n int not null,
  locale text not null check (locale in ('en', 'ar')),
  input text not null,
  expectations jsonb not null,
  persona_id uuid not null,
  severity text not null check (severity in ('critical', 'blocking', 'warning')),
  classification text not null check (classification in ('public', 'internal', 'confidential', 'restricted')),
  status text not null check (status in ('pending', 'running', 'done', 'error', 'skipped')),
  agent_run_id uuid null,
  outcome text null,
  answer text null,
  metrics jsonb null,
  verdicts jsonb null,
  error text null,
  model text null,
  latency_ms int null,
  tokens int null,
  finished_at timestamptz null,
  primary key (tenant_id, run_id, n),
  foreign key (tenant_id, run_id) references evaluation.run (tenant_id, id) on delete cascade
);
create index case_result_pending on evaluation.case_result (tenant_id, run_id, n) where status = 'pending';
select platform.enable_tenant_rls('evaluation.case_result');
grant select, insert, update, delete on evaluation.case_result to tb_app;
