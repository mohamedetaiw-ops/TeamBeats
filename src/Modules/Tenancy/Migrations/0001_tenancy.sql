-- Specs 001 and 002: tenant registry, settings, admin appointments, memberships, organization hierarchy.
create schema tenancy;
grant usage on schema tenancy to tb_app;

-- Platform registry (RLS exception, see rls-exceptions.md). Only platform endpoints and the host resolver read it.
create table tenancy.tenant (
  id uuid primary key,
  key text not null unique check (key ~ '^[a-z0-9][a-z0-9-]{1,38}[a-z0-9]$'),
  display_name text not null,
  status text not null check (status in ('provisioning', 'active', 'suspended', 'closed')),
  created_at timestamptz not null default now(),
  status_changed_at timestamptz not null default now(),
  closed_at timestamptz null,
  deletion_due_at timestamptz null,
  closed_retention_days int not null default 90 check (closed_retention_days > 0)
);
grant select, insert, update on tenancy.tenant to tb_app;

create table tenancy.tenant_settings (
  tenant_id uuid primary key,
  default_locale text not null,
  enabled_locales text[] not null,
  time_zone text not null,
  numbering_system text not null default 'latn' check (numbering_system in ('latn', 'arab')),
  primary_color text null check (primary_color is null or primary_color ~ '^#[0-9a-fA-F]{6}$'),
  logo_object_key text null,
  logo_content_type text null,
  host_name text null,
  row_version int not null default 1,
  check (default_locale = any(enabled_locales))
);
select platform.enable_tenant_rls('tenancy.tenant_settings');
grant select, insert, update on tenancy.tenant_settings to tb_app;

create table tenancy.tenant_admin_appointment (
  tenant_id uuid not null,
  id uuid not null,
  invited_email text not null,
  user_id uuid null,
  status text not null check (status in ('pending', 'active', 'revoked')),
  appointed_by uuid null,
  created_at timestamptz not null default now(),
  activated_at timestamptz null,
  primary key (tenant_id, id)
);
create unique index tenant_admin_appointment_email on tenancy.tenant_admin_appointment (tenant_id, invited_email) where status <> 'revoked';
select platform.enable_tenant_rls('tenancy.tenant_admin_appointment');
grant select, insert, update on tenancy.tenant_admin_appointment to tb_app;

-- Tenant membership: where a tenant-scoped user may go (spec 003 FR-004). The user record lives in Identity.
create table tenancy.tenant_membership (
  tenant_id uuid not null,
  user_id uuid not null,
  membership_type text not null check (membership_type in ('employee', 'contractor', 'guest')),
  status text not null check (status in ('active', 'disabled')),
  preferred_org_unit_id uuid null,
  created_at timestamptz not null default now(),
  status_changed_at timestamptz not null default now(),
  primary key (tenant_id, user_id)
);
select platform.enable_tenant_rls('tenancy.tenant_membership');
grant select, insert, update on tenancy.tenant_membership to tb_app;

-- Whether a membership type receives the default Employee role (spec 004 section 6).
create table tenancy.membership_type_policy (
  tenant_id uuid not null,
  membership_type text not null,
  default_employee_role boolean not null,
  primary key (tenant_id, membership_type)
);
select platform.enable_tenant_rls('tenancy.membership_type_policy');
grant select, insert, update on tenancy.membership_type_policy to tb_app;

-- Bumped on any hierarchy or membership change; part of authorization cache keys.
create table tenancy.hierarchy_version (
  tenant_id uuid primary key,
  version bigint not null
);
select platform.enable_tenant_rls('tenancy.hierarchy_version');
grant select, insert, update on tenancy.hierarchy_version to tb_app;

-- Tenant-defined unit types. Built-in types are defined in code (spec 002 FR-002).
create table tenancy.org_unit_type (
  tenant_id uuid not null,
  key text not null check (key ~ '^[a-z][a-z0-9_]{1,39}$'),
  names jsonb not null,
  created_at timestamptz not null default now(),
  primary key (tenant_id, key)
);
select platform.enable_tenant_rls('tenancy.org_unit_type');
grant select, insert on tenancy.org_unit_type to tb_app;

create table tenancy.org_unit (
  tenant_id uuid not null,
  id uuid not null,
  parent_id uuid null,
  type_key text not null,
  code text not null check (code ~ '^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$'),
  status text not null check (status in ('active', 'inactive')),
  external_source text null,
  external_id text null,
  created_at timestamptz not null default now(),
  primary key (tenant_id, id),
  -- Composite FK: a parent in another tenant is impossible at the database level (spec 002 TEN-005).
  foreign key (tenant_id, parent_id) references tenancy.org_unit (tenant_id, id)
);
create unique index org_unit_code on tenancy.org_unit (tenant_id, lower(code));
create unique index org_unit_external on tenancy.org_unit (tenant_id, external_source, external_id) where external_id is not null;
create index org_unit_parent on tenancy.org_unit (tenant_id, parent_id);
select platform.enable_tenant_rls('tenancy.org_unit');
grant select, insert, update on tenancy.org_unit to tb_app;

create table tenancy.org_unit_name (
  tenant_id uuid not null,
  org_unit_id uuid not null,
  locale text not null,
  name text not null check (length(name) between 1 and 200),
  primary key (tenant_id, org_unit_id, locale),
  foreign key (tenant_id, org_unit_id) references tenancy.org_unit (tenant_id, id)
);
select platform.enable_tenant_rls('tenancy.org_unit_name');
grant select, insert, update, delete on tenancy.org_unit_name to tb_app;

create table tenancy.org_closure (
  tenant_id uuid not null,
  ancestor_id uuid not null,
  descendant_id uuid not null,
  depth int not null,
  primary key (tenant_id, ancestor_id, descendant_id),
  foreign key (tenant_id, ancestor_id) references tenancy.org_unit (tenant_id, id),
  foreign key (tenant_id, descendant_id) references tenancy.org_unit (tenant_id, id)
);
create index org_closure_descendant on tenancy.org_closure (tenant_id, descendant_id);
select platform.enable_tenant_rls('tenancy.org_closure');
grant select, insert, delete on tenancy.org_closure to tb_app;

create table tenancy.org_membership (
  tenant_id uuid not null,
  id uuid not null,
  user_id uuid not null,
  org_unit_id uuid not null,
  type text not null check (type in ('primary', 'secondary', 'secondment', 'other')),
  is_primary boolean not null,
  valid_from timestamptz not null,
  valid_to timestamptz null,
  primary key (tenant_id, id),
  foreign key (tenant_id, user_id) references tenancy.tenant_membership (tenant_id, user_id),
  foreign key (tenant_id, org_unit_id) references tenancy.org_unit (tenant_id, id)
);
create unique index org_membership_one_primary on tenancy.org_membership (tenant_id, user_id) where is_primary and valid_to is null;
create index org_membership_user on tenancy.org_membership (tenant_id, user_id);
create index org_membership_unit on tenancy.org_membership (tenant_id, org_unit_id);
select platform.enable_tenant_rls('tenancy.org_membership');
grant select, insert, update on tenancy.org_membership to tb_app;
