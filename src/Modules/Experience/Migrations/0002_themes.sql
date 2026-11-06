-- Spec 033 (R1): brand profiles with immutable versions, assignments and overrides per scope, and locks. A null
-- scope_org_unit_id means the tenant; org units are referenced by immutable ID (FR-026).
create table experience.brand (
  tenant_id uuid not null,
  id uuid not null,
  name text not null check (length(name) between 1 and 80),
  current_version int not null,
  seeded boolean not null default false,
  created_at timestamptz not null default now(),
  primary key (tenant_id, id)
);
-- One seeded default brand per tenant, from the spec 001 logo and primary color.
create unique index brand_seeded_once on experience.brand (tenant_id) where seeded;
select platform.enable_tenant_rls('experience.brand');
grant select, insert, update on experience.brand to tb_app;

-- Versions never change once written: the application role can insert and read only.
create table experience.brand_version (
  tenant_id uuid not null,
  brand_id uuid not null,
  version int not null,
  tokens jsonb not null,
  assets jsonb not null,
  strict_contrast boolean not null default false,
  created_by uuid null,
  created_at timestamptz not null default now(),
  primary key (tenant_id, brand_id, version),
  foreign key (tenant_id, brand_id) references experience.brand (tenant_id, id)
);
select platform.enable_tenant_rls('experience.brand_version');
grant select, insert on experience.brand_version to tb_app;

create table experience.brand_assignment (
  tenant_id uuid not null,
  scope_org_unit_id uuid null,
  brand_id uuid null,
  overrides jsonb not null default '{}',
  changed_by uuid null,
  changed_at timestamptz not null default now(),
  foreign key (tenant_id, brand_id) references experience.brand (tenant_id, id),
  check (scope_org_unit_id is not null or brand_id is not null)
);
create unique index brand_assignment_scope on experience.brand_assignment
  (tenant_id, coalesce(scope_org_unit_id, '00000000-0000-0000-0000-000000000000'::uuid));
select platform.enable_tenant_rls('experience.brand_assignment');
grant select, insert, update, delete on experience.brand_assignment to tb_app;

create table experience.experience_lock (
  tenant_id uuid not null,
  scope_org_unit_id uuid null,
  setting text not null check (setting ~ '^[a-z0-9_.]+$' and length(setting) <= 100),
  locked_by uuid null,
  locked_at timestamptz not null default now()
);
create unique index experience_lock_scope on experience.experience_lock
  (tenant_id, coalesce(scope_org_unit_id, '00000000-0000-0000-0000-000000000000'::uuid), setting);
select platform.enable_tenant_rls('experience.experience_lock');
grant select, insert, delete on experience.experience_lock to tb_app;

-- Bumped on every brand, assignment or lock change; part of the theme cache key, so a change shows on the next request.
create table experience.theme_state (
  tenant_id uuid not null primary key,
  version bigint not null default 0
);
select platform.enable_tenant_rls('experience.theme_state');
grant select, insert, update on experience.theme_state to tb_app;
