-- Spec 029 (R2) slice 1: project and team spaces, their members, the tenant's space rules and templates, and the task
-- lists, columns and tasks a template starts a space with. Who may read, contribute to or own a space is decided by the
-- space roles in authorization (spec 004), assigned on the space; space_member mirrors them for lists and counts and is
-- written in the same transaction. Every table is tenant-owned with forced row-level security.
create schema spaces;
grant usage on schema spaces to tb_app;

-- The tenant's space rules (FR-017), versioned and changed with the version in the body. No row means the defaults:
-- everyone with spaces.create may create, guests off, guest access for 90 days.
create table spaces.setting (
  tenant_id uuid not null primary key,
  creation_units uuid[] not null default '{}',
  guests_allowed boolean not null,
  guest_days int not null check (guest_days between 7 and 365),
  version int not null default 1,
  updated_by uuid not null,
  updated_at timestamptz not null
);
select platform.enable_tenant_rls('spaces.setting');
grant select, insert, update on spaces.setting to tb_app;

-- A template the tenant adds (FR-012). The built-in project and team templates live in code. definition holds the
-- lists with their columns and the starter tasks; a space copies it, so changing a template never changes a space.
create table spaces.template (
  tenant_id uuid not null,
  id uuid not null,
  name text not null,
  kind text not null check (kind in ('project', 'team')),
  definition jsonb not null,
  status text not null default 'active' check (status in ('active', 'retired')),
  version int not null default 1,
  created_by uuid not null,
  created_at timestamptz not null,
  updated_at timestamptz not null,
  primary key (tenant_id, id)
);
select platform.enable_tenant_rls('spaces.template');
grant select, insert, update on spaces.template to tb_app;

-- A space (FR-001). Projects have dates and a status; teams are ongoing. Public spaces are readable by the tenant, and
-- people may join as members when allow_join is on; private spaces are listed but read only by members; secret spaces
-- are known only to their members. org_unit_id is the creator's primary unit, kept for reports.
create table spaces.space (
  tenant_id uuid not null,
  id uuid not null,
  kind text not null check (kind in ('project', 'team')),
  visibility text not null check (visibility in ('public', 'private', 'secret')),
  name text not null,
  description text not null default '',
  allow_join boolean not null default false,
  classification text not null default 'internal' check (classification in ('internal', 'confidential')),
  starts_on date null,
  ends_on date null,
  template text not null,
  org_unit_id uuid null,
  status text not null default 'active' check (status in ('active', 'archived')),
  created_by uuid not null,
  created_at timestamptz not null,
  updated_at timestamptz not null,
  archived_by uuid null,
  archived_at timestamptz null,
  row_version int not null default 1,
  primary key (tenant_id, id),
  check (kind = 'project' or (starts_on is null and ends_on is null)),
  check (starts_on is null or ends_on is null or ends_on >= starts_on)
);
create index space_status on spaces.space (tenant_id, status, visibility);
select platform.enable_tenant_rls('spaces.space');
grant select, insert, update on spaces.space to tb_app;

-- The people of a space with their role. guest and expires_at are for guests (US4, a later slice).
create table spaces.space_member (
  tenant_id uuid not null,
  space_id uuid not null,
  user_id uuid not null,
  role text not null check (role in ('owner', 'member', 'viewer')),
  guest boolean not null default false,
  expires_at timestamptz null,
  added_by uuid null,
  added_at timestamptz not null,
  primary key (tenant_id, space_id, user_id),
  foreign key (tenant_id, space_id) references spaces.space (tenant_id, id)
);
create index space_member_user on spaces.space_member (tenant_id, user_id);
select platform.enable_tenant_rls('spaces.space_member');
grant select, insert, update, delete on spaces.space_member to tb_app;

-- Task lists of a space and the columns of each list's board (FR-004). A done column completes the tasks moved to it.
create table spaces.task_list (
  tenant_id uuid not null,
  id uuid not null,
  space_id uuid not null,
  name text not null,
  position int not null,
  primary key (tenant_id, id),
  foreign key (tenant_id, space_id) references spaces.space (tenant_id, id)
);
create index task_list_space on spaces.task_list (tenant_id, space_id, position);
select platform.enable_tenant_rls('spaces.task_list');
grant select, insert, update, delete on spaces.task_list to tb_app;

create table spaces.task_column (
  tenant_id uuid not null,
  id uuid not null,
  list_id uuid not null,
  name text not null,
  position int not null,
  done boolean not null default false,
  primary key (tenant_id, id),
  foreign key (tenant_id, list_id) references spaces.task_list (tenant_id, id)
);
create index task_column_list on spaces.task_column (tenant_id, list_id, position);
select platform.enable_tenant_rls('spaces.task_column');
grant select, insert, update, delete on spaces.task_column to tb_app;

-- A task (FR-004). Assignees, checklist items, comments and attachments come with the tasks slice.
create table spaces.task (
  tenant_id uuid not null,
  id uuid not null,
  space_id uuid not null,
  list_id uuid not null,
  column_id uuid not null,
  title text not null,
  description text not null default '',
  priority text not null default 'normal' check (priority in ('low', 'normal', 'high', 'urgent')),
  labels text[] not null default '{}',
  due_on date null,
  position int not null,
  status text not null default 'open' check (status in ('open', 'done')),
  completed_by uuid null,
  completed_at timestamptz null,
  created_by uuid not null,
  created_at timestamptz not null,
  updated_at timestamptz not null,
  row_version int not null default 1,
  primary key (tenant_id, id),
  foreign key (tenant_id, space_id) references spaces.space (tenant_id, id),
  foreign key (tenant_id, list_id) references spaces.task_list (tenant_id, id),
  foreign key (tenant_id, column_id) references spaces.task_column (tenant_id, id)
);
create index task_space on spaces.task (tenant_id, space_id, list_id, column_id, position);
select platform.enable_tenant_rls('spaces.task');
grant select, insert, update, delete on spaces.task to tb_app;
