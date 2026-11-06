-- Spec 003 User Story 6 (R1 for Entra ID): keep users and groups in step with the directory.
alter table identity.user_account drop constraint user_account_created_via_check;
alter table identity.user_account add constraint user_account_created_via_check
  check (created_via in ('jit', 'admin', 'scim', 'dev', 'directory'));
-- Fields the directory owns for this user; read-only in the product (FR-014).
alter table identity.user_account add column owner_fields text[] not null default '{}';

-- Directory sync settings for a connection owned by Integrations (spec 006). The delta cursors are Graph delta links.
create table identity.directory_connection (
  tenant_id uuid not null,
  id uuid not null,
  connection_id uuid not null,
  user_scope jsonb not null default '{"mode":"all","groups":[]}',
  sync_groups text[] not null default '{}',
  schedule_minutes int not null default 30 check (schedule_minutes between 5 and 1440),
  deletion_threshold_pct int not null default 10 check (deletion_threshold_pct between 1 and 100),
  users_cursor text null,
  groups_cursor text null,
  consecutive_failures int not null default 0,
  last_run_at timestamptz null,
  next_run_at timestamptz not null default now(),
  created_at timestamptz not null default now(),
  updated_at timestamptz not null default now(),
  primary key (tenant_id, id),
  unique (tenant_id, connection_id)
);
select platform.enable_tenant_rls('identity.directory_connection');
grant select, insert, update, delete on identity.directory_connection to tb_app;

-- Synced users keyed by the directory's immutable ID (Entra oid), never by email or UPN.
create table identity.directory_user_link (
  tenant_id uuid not null,
  connection_id uuid not null,
  directory_id text not null,
  user_id uuid not null,
  disabled_by_directory boolean not null default false,
  updated_at timestamptz not null default now(),
  primary key (tenant_id, connection_id, directory_id),
  unique (tenant_id, user_id),
  foreign key (tenant_id, user_id) references identity.user_account (tenant_id, id)
);
select platform.enable_tenant_rls('identity.directory_user_link');
grant select, insert, update, delete on identity.directory_user_link to tb_app;

-- Mirror of every directory account seen, so the scope can be re-evaluated without re-reading the directory.
create table identity.directory_user (
  tenant_id uuid not null,
  connection_id uuid not null,
  directory_id text not null,
  account_enabled boolean not null,
  attributes jsonb not null default '{}',
  primary key (tenant_id, connection_id, directory_id)
);
select platform.enable_tenant_rls('identity.directory_user');
grant select, insert, update, delete on identity.directory_user to tb_app;

-- Mirror of directory group membership, used for the user scope and for synced groups.
create table identity.directory_group (
  tenant_id uuid not null,
  connection_id uuid not null,
  directory_id text not null,
  display_name text null,
  members text[] not null default '{}',
  primary key (tenant_id, connection_id, directory_id)
);
select platform.enable_tenant_rls('identity.directory_group');
grant select, insert, update, delete on identity.directory_group to tb_app;

-- Directory groups kept in step as product groups (source idp in authz), so a deselected group can be emptied.
create table identity.directory_group_link (
  tenant_id uuid not null,
  connection_id uuid not null,
  directory_id text not null,
  group_id uuid not null,
  primary key (tenant_id, connection_id, directory_id)
);
select platform.enable_tenant_rls('identity.directory_group_link');
grant select, insert, update, delete on identity.directory_group_link to tb_app;

-- Run log: counts and IDs only (spec 003 section 8).
create table identity.directory_sync_run (
  tenant_id uuid not null,
  id uuid not null,
  connection_id uuid not null,
  started_at timestamptz not null default now(),
  finished_at timestamptz null,
  outcome text not null check (outcome in ('running', 'succeeded', 'failed', 'stopped_threshold')),
  reason text null,
  created int not null default 0,
  updated int not null default 0,
  disabled int not null default 0,
  reenabled int not null default 0,
  groups int not null default 0,
  skipped int not null default 0,
  primary key (tenant_id, id)
);
create index directory_sync_run_recent on identity.directory_sync_run (tenant_id, connection_id, started_at desc);
select platform.enable_tenant_rls('identity.directory_sync_run');
grant select, insert, update, delete on identity.directory_sync_run to tb_app;
