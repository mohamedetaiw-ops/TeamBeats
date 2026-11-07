-- Spec 010 (R1): profiles, the reporting line, the field policy and locations. Every table is tenant-owned with forced
-- row-level security. Account values (name, email, status, units) are a read model refreshed from the Identity and
-- Tenancy contracts; directory values come from directory sync; employee and administrator values are owned here.
create schema people;
grant usage on schema people to tb_app;

create table people.settings (
  tenant_id uuid not null primary key,
  newcomer_days int not null default 30 check (newcomer_days between 7 and 90),
  allow_newcomer_opt_out boolean not null default false,
  location_attribute text not null default 'officeLocation',
  -- The hierarchy version the account read model was last refreshed at, and the last directory refresh.
  hierarchy_version bigint not null default -1,
  directory_synced_at timestamptz null,
  version int not null default 1,
  updated_at timestamptz not null default now()
);
select platform.enable_tenant_rls('people.settings');
grant select, insert, update on people.settings to tb_app;

create table people.profile (
  tenant_id uuid not null,
  user_id uuid not null,
  display_name text not null,
  names jsonb not null default '{}'::jsonb,
  email text not null,
  membership_type text not null,
  active boolean not null,
  primary_unit_id uuid null,
  unit_ids uuid[] not null default '{}',
  directory_id text null,
  directory_values jsonb not null default '{}'::jsonb,
  own_values jsonb not null default '{}'::jsonb,
  admin_values jsonb not null default '{}'::jsonb,
  location_key text null,
  hire_date date null,
  first_seen date not null default current_date,
  newcomer_opt_out boolean not null default false,
  -- Normalized text for people search (spec 010 FR-006); built by the application.
  search_name text not null default '',
  search_skeleton text not null default '',
  search_local text not null default '',
  search_fields jsonb not null default '{}'::jsonb,
  version int not null default 1,
  updated_at timestamptz not null default now(),
  primary key (tenant_id, user_id)
);
create index profile_directory on people.profile (tenant_id, directory_id) where directory_id is not null;
create index profile_location on people.profile (tenant_id, location_key) where location_key is not null;
select platform.enable_tenant_rls('people.profile');
grant select, insert, update on people.profile to tb_app;

create table people.reporting_line (
  tenant_id uuid not null,
  id uuid not null,
  user_id uuid not null,
  manager_user_id uuid null,
  source text not null check (source in ('directory', 'manual')),
  valid_from timestamptz not null,
  valid_to timestamptz null,
  changed_by uuid null,
  primary key (tenant_id, id),
  check (manager_user_id is null or manager_user_id <> user_id)
);
create unique index reporting_line_current on people.reporting_line (tenant_id, user_id) where valid_to is null;
create index reporting_line_manager on people.reporting_line (tenant_id, manager_user_id) where valid_to is null;
select platform.enable_tenant_rls('people.reporting_line');
grant select, insert, update on people.reporting_line to tb_app;

create table people.field_policy (
  tenant_id uuid not null,
  field_key text not null,
  source text not null check (source in ('directory', 'person', 'admin')),
  visibility text not null check (visibility in ('everyone', 'subtree', 'managers_hr', 'hr', 'private')),
  is_custom boolean not null default false,
  names jsonb not null default '{}'::jsonb,
  position int not null default 0,
  primary key (tenant_id, field_key)
);
select platform.enable_tenant_rls('people.field_policy');
grant select, insert, update, delete on people.field_policy to tb_app;

create table people.location (
  tenant_id uuid not null,
  key text not null,
  label text not null,
  names jsonb not null default '{}'::jsonb,
  created_at timestamptz not null default now(),
  primary key (tenant_id, key)
);
select platform.enable_tenant_rls('people.location');
grant select, insert, update on people.location to tb_app;
