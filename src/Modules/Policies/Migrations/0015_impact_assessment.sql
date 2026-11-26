-- Spec 025 R3 US9 (FR-023): the AI impact assessment of a working version, kept per version and locale with the hash
-- of what it read, so every approver reads the same one and a later edit asks again. Only clause numbers, control
-- codes, policy ids and the reasons are kept.
create table policies.impact_assessment (
  tenant_id uuid not null,
  version_id uuid not null,
  locale text not null,
  basis_hash text not null,
  summary text not null check (length(summary) between 1 and 4000),
  impacts jsonb not null default '[]',
  model text not null,
  created_by uuid not null,
  created_at timestamptz not null,
  primary key (tenant_id, version_id, locale),
  foreign key (tenant_id, version_id) references policies.policy_version (tenant_id, id)
);
select platform.enable_tenant_rls('policies.impact_assessment');
grant select, insert, update on policies.impact_assessment to tb_app;
