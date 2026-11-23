-- Spec 034 R2 T018 (US7, FR-023): continuous production evaluation. The tenant's sampling policy, the people who opted
-- out, a counter per agent and day for the daily cap, and the sampled runs with their scores. A sample names the agent,
-- its version and the organization unit, never the person. Its content (question, answer and evidence) is kept only
-- until it is scored, or for the tenant's retention period when that is on and the run was Public or Internal.
create table evaluation.production_policy (
  tenant_id uuid not null primary key,
  enabled boolean not null,
  rate_percent numeric(5, 2) not null check (rate_percent >= 0 and rate_percent <= 100),
  daily_cap int not null check (daily_cap between 1 and 1000),
  retention_days int not null check (retention_days between 0 and 30),
  updated_by uuid not null,
  updated_at timestamptz not null
);
select platform.enable_tenant_rls('evaluation.production_policy');
grant select, insert, update on evaluation.production_policy to tb_app;

create table evaluation.production_opt_out (
  tenant_id uuid not null,
  user_id uuid not null,
  at timestamptz not null,
  primary key (tenant_id, user_id)
);
select platform.enable_tenant_rls('evaluation.production_opt_out');
grant select, insert, delete on evaluation.production_opt_out to tb_app;

create table evaluation.production_day (
  tenant_id uuid not null,
  agent_id uuid not null,
  day date not null,
  sampled int not null,
  primary key (tenant_id, agent_id, day)
);
select platform.enable_tenant_rls('evaluation.production_day');
grant select, insert, update, delete on evaluation.production_day to tb_app;

create table evaluation.production_sample (
  tenant_id uuid not null,
  id uuid not null,
  agent_run_id uuid not null,
  agent_id uuid not null,
  agent_version int not null,
  org_unit_id uuid not null,
  language text not null,
  classification text not null,
  -- full: content and judged metrics; deterministic: no eligible judge was found; metadata: Restricted, no content read.
  mode text not null check (mode in ('full', 'deterministic', 'metadata')),
  status text not null check (status in ('queued', 'scored')),
  run_status text not null,
  outcome text null,
  model text null,
  input_tokens int not null,
  output_tokens int not null,
  latency_ms int not null,
  tool_calls int not null,
  denials int not null,
  metrics jsonb not null default '{}',
  content jsonb null,
  content_expires_at timestamptz null,
  sampled_at timestamptz not null,
  scored_at timestamptz null,
  primary key (tenant_id, id)
);
create unique index production_sample_run on evaluation.production_sample (tenant_id, agent_run_id);
create index production_sample_agent on evaluation.production_sample (tenant_id, agent_id, sampled_at desc);
create index production_sample_content on evaluation.production_sample (tenant_id, content_expires_at) where content is not null;
select platform.enable_tenant_rls('evaluation.production_sample');
grant select, insert, update, delete on evaluation.production_sample to tb_app;
