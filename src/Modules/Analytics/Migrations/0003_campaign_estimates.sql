-- Spec 021 R2 T011 (US2 scenario 2, FR-009): the tenant's effort estimates for return on effort, as versioned
-- configuration (constitution XXIX). Every change is a new version row; the highest version is in force.
create table analytics.effort_estimate (
  tenant_id uuid not null,
  version int not null check (version >= 1),
  schema_version int not null default 1 check (schema_version = 1),
  document jsonb not null,
  changed_at timestamptz not null,
  changed_by uuid not null,
  primary key (tenant_id, version)
);
select platform.enable_tenant_rls('analytics.effort_estimate');
grant select, insert on analytics.effort_estimate to tb_app;
