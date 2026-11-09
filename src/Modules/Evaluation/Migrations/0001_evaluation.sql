-- Spec 034 T001 (R1, slice 1): the evaluation registry. Suites per agent with versions, golden datasets with versions
-- and cases, synthetic personas, imports waiting for confirmation and the tenant's evaluation settings. Every table is
-- tenant-owned with forced RLS (FR-021). Platform suites, metric definitions and mandatory safety suites ship as
-- read-only data with the module and hold no tenant data.
create schema evaluation;
grant usage on schema evaluation to tb_app;

-- Tenant evaluation settings (US10): thresholds that may only be stricter than the platform defaults.
create table evaluation.settings (
  tenant_id uuid not null primary key,
  version int not null,
  thresholds jsonb not null,
  updated_by uuid not null,
  updated_at timestamptz not null
);
select platform.enable_tenant_rls('evaluation.settings');
grant select, insert, update on evaluation.settings to tb_app;

-- An agent's own suite (US1 scenario 1). One per agent in R1; inherited platform and mandatory suites are not copied.
create table evaluation.suite (
  tenant_id uuid not null,
  id uuid not null,
  capability text not null check (capability in ('agent')),
  agent_id uuid not null,
  org_unit_id uuid not null,
  current_version int not null,
  created_by uuid not null,
  created_at timestamptz not null,
  updated_at timestamptz not null,
  primary key (tenant_id, id)
);
create unique index suite_agent on evaluation.suite (tenant_id, agent_id);
select platform.enable_tenant_rls('evaluation.suite');
grant select, insert, update on evaluation.suite to tb_app;

-- Every save is a new version (US1 scenario 3); versions never change, so results can always name the one they used.
create table evaluation.suite_version (
  tenant_id uuid not null,
  suite_id uuid not null,
  number int not null,
  metrics jsonb not null,
  datasets uuid[] not null,
  created_by uuid not null,
  created_at timestamptz not null,
  primary key (tenant_id, suite_id, number)
);
select platform.enable_tenant_rls('evaluation.suite_version');
grant select, insert on evaluation.suite_version to tb_app;

-- A golden dataset of an agent (US2). It takes the highest classification of its cases (section 15).
create table evaluation.dataset (
  tenant_id uuid not null,
  id uuid not null,
  agent_id uuid not null,
  org_unit_id uuid not null,
  names jsonb not null,
  current_version int not null,
  created_by uuid not null,
  created_at timestamptz not null,
  updated_at timestamptz not null,
  primary key (tenant_id, id)
);
create index dataset_agent on evaluation.dataset (tenant_id, agent_id);
select platform.enable_tenant_rls('evaluation.dataset');
grant select, insert, update on evaluation.dataset to tb_app;

-- Dataset versions are immutable (FR-005): an edit or an import always makes the next version.
create table evaluation.dataset_version (
  tenant_id uuid not null,
  dataset_id uuid not null,
  number int not null,
  case_count int not null,
  classification text not null check (classification in ('public', 'internal', 'confidential', 'restricted')),
  locales text[] not null,
  source text not null check (source in ('edit', 'import')),
  created_by uuid not null,
  created_at timestamptz not null,
  primary key (tenant_id, dataset_id, number)
);
select platform.enable_tenant_rls('evaluation.dataset_version');
grant select, insert on evaluation.dataset_version to tb_app;

create table evaluation.eval_case (
  tenant_id uuid not null,
  dataset_id uuid not null,
  version int not null,
  n int not null,
  input text not null,
  locale text not null,
  persona_id uuid null,
  expectations jsonb not null,
  tags text[] not null,
  severity text not null check (severity in ('critical', 'blocking', 'warning')),
  classification text not null check (classification in ('public', 'internal', 'confidential', 'restricted')),
  primary key (tenant_id, dataset_id, version, n)
);
select platform.enable_tenant_rls('evaluation.eval_case');
grant select, insert on evaluation.eval_case to tb_app;

-- A persona (US2 scenario 2): a synthetic test identity with an org unit, roles and a membership type. The identity
-- lives in Identity (flagged as a test identity) and cannot sign in or appear in the directory.
create table evaluation.persona (
  tenant_id uuid not null,
  id uuid not null,
  names jsonb not null,
  org_unit_id uuid not null,
  roles text[] not null,
  membership_type text not null check (membership_type in ('employee', 'contractor', 'guest')),
  user_id uuid not null,
  created_by uuid not null,
  created_at timestamptz not null,
  primary key (tenant_id, id)
);
select platform.enable_tenant_rls('evaluation.persona');
grant select, insert on evaluation.persona to tb_app;

-- An import waiting for confirmation (US2 scenario 4): the valid cases and the rows refused with reasons. Nothing is
-- added to the dataset until the person confirms; imports older than a day are removed.
create table evaluation.import (
  tenant_id uuid not null,
  id uuid not null,
  dataset_id uuid not null,
  base_version int not null,
  cases jsonb not null,
  errors jsonb not null,
  created_by uuid not null,
  created_at timestamptz not null,
  confirmed_at timestamptz null,
  primary key (tenant_id, id)
);
select platform.enable_tenant_rls('evaluation.import');
grant select, insert, update, delete on evaluation.import to tb_app;
