-- Spec 002 User Story 5 (R1 for Entra ID): the organization hierarchy mapped from the directory.
alter table tenancy.org_unit add column owner text not null default 'product' check (owner in ('product', 'directory', 'hr'));
alter table tenancy.org_unit add column owner_connection_id uuid null;
alter table tenancy.org_membership add column owner text not null default 'product' check (owner in ('product', 'directory', 'hr'));
alter table tenancy.org_membership add column owner_connection_id uuid null;
create index org_membership_owner on tenancy.org_membership (tenant_id, owner_connection_id) where owner_connection_id is not null;

-- One mapping per directory connection. Settings are validated by the service (constitution XXIX).
create table tenancy.directory_mapping (
  tenant_id uuid not null,
  id uuid not null,
  connection_id uuid not null,
  strategy text not null check (strategy in ('ou_path', 'attributes', 'admin_units', 'groups')),
  settings jsonb not null default '{}',
  deletion_threshold_pct int not null default 10 check (deletion_threshold_pct between 1 and 100),
  previewed_at timestamptz null,
  approved_at timestamptz null,
  created_at timestamptz not null default now(),
  updated_at timestamptz not null default now(),
  primary key (tenant_id, id),
  unique (tenant_id, connection_id)
);
select platform.enable_tenant_rls('tenancy.directory_mapping');
grant select, insert, update, delete on tenancy.directory_mapping to tb_app;

-- Preview and apply runs. The diff holds unit keys, unit IDs and user IDs only, never directory personal data.
create table tenancy.directory_mapping_run (
  tenant_id uuid not null,
  id uuid not null,
  connection_id uuid not null,
  mode text not null check (mode in ('preview', 'apply')),
  started_at timestamptz not null default now(),
  outcome text not null check (outcome in ('succeeded', 'stopped_threshold')),
  counts jsonb not null default '{}',
  diff jsonb not null default '{}',
  primary key (tenant_id, id)
);
create index directory_mapping_run_recent on tenancy.directory_mapping_run (tenant_id, connection_id, started_at desc);
select platform.enable_tenant_rls('tenancy.directory_mapping_run');
grant select, insert, delete on tenancy.directory_mapping_run to tb_app;
