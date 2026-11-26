-- Spec 029 R3 US10 (FR-023 to FR-025): a task list kept in step with a Planner plan or a Jira project. The connection
-- belongs to Integrations; Spaces keeps only its id. Every table is tenant-owned with forced row-level security.

-- One link per task list, and one active link per plan or project. Ended links stay for their history.
create table spaces.sync_link (
  tenant_id uuid not null,
  id uuid not null,
  space_id uuid not null,
  list_id uuid not null,
  connection_id uuid not null,
  kind text not null check (kind in ('tasks_planner', 'tasks_jira')),
  container_id text not null,
  container_name text not null,
  status text not null check (status in ('active', 'ended')),
  last_run_at timestamptz null,
  next_run_at timestamptz not null,
  created_by uuid not null,
  created_at timestamptz not null,
  ended_at timestamptz null,
  primary key (tenant_id, id),
  foreign key (tenant_id, space_id) references spaces.space (tenant_id, id),
  foreign key (tenant_id, list_id) references spaces.task_list (tenant_id, id)
);
create unique index sync_link_list on spaces.sync_link (tenant_id, list_id) where status = 'active';
create unique index sync_link_container on spaces.sync_link (tenant_id, connection_id, container_id) where status = 'active';
create index sync_link_due on spaces.sync_link (tenant_id, next_run_at) where status = 'active';
select platform.enable_tenant_rls('spaces.sync_link');
grant select, insert, update, delete on spaces.sync_link to tb_app;

-- A task and its twin in the tracker, with the versions both had at the last sync.
create table spaces.sync_item (
  tenant_id uuid not null,
  link_id uuid not null,
  task_id uuid not null,
  external_id text not null,
  local_version int not null,
  external_version text not null,
  synced_at timestamptz not null,
  primary key (tenant_id, link_id, task_id),
  foreign key (tenant_id, link_id) references spaces.sync_link (tenant_id, id),
  foreign key (tenant_id, task_id) references spaces.task (tenant_id, id) on delete cascade
);
create unique index sync_item_external on spaces.sync_item (tenant_id, link_id, external_id);
select platform.enable_tenant_rls('spaces.sync_item');
grant select, insert, update, delete on spaces.sync_item to tb_app;

-- What each run did and what went wrong (US10 scenarios 3 to 5), kept 90 days.
create table spaces.sync_event (
  tenant_id uuid not null,
  id uuid not null,
  link_id uuid not null,
  run_id uuid not null,
  kind text not null check (kind in ('run', 'conflict', 'failed', 'skipped_assignee', 'unlinked_task')),
  outcome text null,
  task_title text null,
  detail text null,
  pulled int not null default 0,
  pushed int not null default 0,
  at timestamptz not null,
  primary key (tenant_id, id),
  foreign key (tenant_id, link_id) references spaces.sync_link (tenant_id, id)
);
create index sync_event_link on spaces.sync_event (tenant_id, link_id, at desc);
select platform.enable_tenant_rls('spaces.sync_event');
grant select, insert, update, delete on spaces.sync_event to tb_app;
