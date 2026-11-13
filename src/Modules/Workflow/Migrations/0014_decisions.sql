-- Spec 024 T012 (R2): DMN decision tables. A draft per key that decision owners edit, published as immutable versions
-- that business rule tasks evaluate (the current version when the task is reached). Tenant-owned, forced RLS.
create table workflow.decision_model (
  tenant_id uuid not null,
  key text not null,
  owner_org_unit_id uuid null,
  names jsonb not null default '{}'::jsonb,
  dmn_xml text not null check (length(dmn_xml) <= 1048576),
  problems jsonb not null default '[]'::jsonb,
  created_at timestamptz not null default now(),
  created_by uuid null,
  updated_at timestamptz not null default now(),
  updated_by uuid null,
  primary key (tenant_id, key)
);
select platform.enable_tenant_rls('workflow.decision_model');
grant select, insert, update on workflow.decision_model to tb_app;

create table workflow.decision_definition (
  tenant_id uuid not null,
  key text not null,
  version int not null,
  names jsonb not null default '{}'::jsonb,
  owner_org_unit_id uuid null,
  dmn_xml text not null,
  compiled jsonb not null,
  published_at timestamptz not null default now(),
  published_by uuid null,
  primary key (tenant_id, key, version)
);
select platform.enable_tenant_rls('workflow.decision_definition');
grant select, insert on workflow.decision_definition to tb_app;
