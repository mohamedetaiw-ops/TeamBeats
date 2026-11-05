-- Spec 004: roles, scoped assignments, resource ACLs, inheritance rules, groups.
create schema authz;
grant usage on schema authz to tb_app;

-- Platform permission catalogue (RLS exception). Mirrors Authorization.Contracts.Permissions; a test keeps them equal.
create table authz.permission (
  key text primary key
);
grant select on authz.permission to tb_app;
insert into authz.permission (key) values
  ('tenant.settings_view'), ('tenant.settings_manage'), ('tenant.admins_manage'),
  ('org.view'), ('org.manage'), ('org.membership_manage'),
  ('authz.roles_view'), ('authz.roles_manage'), ('authz.roles_assign'), ('authz.explain'), ('authz.rules_manage'),
  ('identity.users_view'), ('identity.users_manage'), ('identity.config_manage'),
  ('entitlements.view'),
  ('audit.read'), ('audit.export'), ('audit.retention_manage'),
  ('content.view'), ('content.edit'), ('content.publish'),
  ('process.design'), ('workflow.approve'), ('integration.manage'), ('ai.policy_manage');

create table authz.role (
  tenant_id uuid not null,
  id uuid not null,
  key text not null check (key ~ '^[a-z][a-z0-9_]{1,59}$'),
  is_builtin boolean not null,
  is_delegable boolean not null,
  is_delegated_admin boolean not null default false,
  names jsonb null,
  created_at timestamptz not null default now(),
  primary key (tenant_id, id),
  unique (tenant_id, key)
);
select platform.enable_tenant_rls('authz.role');
grant select, insert, update on authz.role to tb_app;

create table authz.role_permission (
  tenant_id uuid not null,
  role_id uuid not null,
  permission_key text not null references authz.permission(key),
  primary key (tenant_id, role_id, permission_key),
  foreign key (tenant_id, role_id) references authz.role (tenant_id, id)
);
select platform.enable_tenant_rls('authz.role_permission');
grant select, insert, delete on authz.role_permission to tb_app;

create table authz.user_group (
  tenant_id uuid not null,
  id uuid not null,
  key text not null,
  name text not null,
  source text not null check (source in ('product', 'idp', 'hr')),
  created_at timestamptz not null default now(),
  primary key (tenant_id, id),
  unique (tenant_id, key)
);
select platform.enable_tenant_rls('authz.user_group');
grant select, insert, update on authz.user_group to tb_app;

create table authz.group_member (
  tenant_id uuid not null,
  group_id uuid not null,
  user_id uuid not null,
  primary key (tenant_id, group_id, user_id),
  foreign key (tenant_id, group_id) references authz.user_group (tenant_id, id)
);
create index group_member_user on authz.group_member (tenant_id, user_id);
select platform.enable_tenant_rls('authz.group_member');
grant select, insert, delete on authz.group_member to tb_app;

create table authz.role_assignment (
  tenant_id uuid not null,
  id uuid not null,
  subject_type text not null check (subject_type in ('user', 'group')),
  subject_id uuid not null,
  role_id uuid not null,
  scope_type text not null check (scope_type in ('tenant', 'org_unit', 'org_subtree', 'resource')),
  scope_id uuid null,
  scope_resource_type text null,
  granted_by uuid null,
  created_at timestamptz not null default now(),
  revoked_at timestamptz null,
  revoked_by uuid null,
  primary key (tenant_id, id),
  foreign key (tenant_id, role_id) references authz.role (tenant_id, id),
  check ((scope_type = 'tenant') = (scope_id is null)),
  check ((scope_type = 'resource') = (scope_resource_type is not null))
);
create index role_assignment_subject on authz.role_assignment (tenant_id, subject_type, subject_id) where revoked_at is null;
create unique index role_assignment_unique on authz.role_assignment
  (tenant_id, subject_type, subject_id, role_id, scope_type, coalesce(scope_id, '00000000-0000-0000-0000-000000000000'::uuid))
  where revoked_at is null;
select platform.enable_tenant_rls('authz.role_assignment');
grant select, insert, update on authz.role_assignment to tb_app;

create table authz.resource_acl (
  tenant_id uuid not null,
  id uuid not null,
  resource_type text not null,
  resource_id uuid not null,
  subject_type text not null check (subject_type in ('user', 'group', 'org_unit', 'org_subtree')),
  subject_id uuid not null,
  permission_key text not null references authz.permission(key),
  granted_by uuid null,
  created_at timestamptz not null default now(),
  primary key (tenant_id, id),
  unique (tenant_id, resource_type, resource_id, subject_type, subject_id, permission_key)
);
create index resource_acl_resource on authz.resource_acl (tenant_id, resource_type, resource_id);
select platform.enable_tenant_rls('authz.resource_acl');
grant select, insert, delete on authz.resource_acl to tb_app;

-- Explicit inheritance rules (spec 004 US2): nothing is inherited without one.
create table authz.inheritance_rule (
  tenant_id uuid not null,
  org_unit_id uuid not null,
  rule text not null check (rule in ('members_view_descendant_restricted')),
  enabled boolean not null,
  updated_at timestamptz not null default now(),
  primary key (tenant_id, org_unit_id, rule)
);
select platform.enable_tenant_rls('authz.inheritance_rule');
grant select, insert, update on authz.inheritance_rule to tb_app;

-- Bumped on every grant change; part of the effective-grants cache key.
create table authz.grants_version (
  tenant_id uuid primary key,
  version bigint not null
);
select platform.enable_tenant_rls('authz.grants_version');
grant select, insert, update on authz.grants_version to tb_app;
