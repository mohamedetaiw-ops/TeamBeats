-- Spec 025 section 11 (R2): an AI change summary between the version in force and the working version, for approvers.
-- One summary per working version and locale, kept with the hash of what it summarized so a later edit asks again.
create table policies.change_summary (
  tenant_id uuid not null,
  version_id uuid not null,
  locale text not null,
  basis_hash text not null,
  summary text not null check (length(summary) between 1 and 4000),
  points jsonb not null default '[]',
  model text not null,
  created_by uuid not null,
  created_at timestamptz not null,
  primary key (tenant_id, version_id, locale),
  foreign key (tenant_id, version_id) references policies.policy_version (tenant_id, id)
);
select platform.enable_tenant_rls('policies.change_summary');
grant select, insert, update on policies.change_summary to tb_app;
