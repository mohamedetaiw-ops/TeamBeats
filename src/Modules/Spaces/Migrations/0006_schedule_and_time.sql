-- Spec 029 R3 US8 and US9 (FR-019 to FR-022): start dates, estimates and "blocked by" links for the timeline, and time
-- logged on tasks. Every table is tenant-owned with forced row-level security.

alter table spaces.task
  add column start_on date null,
  add column estimate_minutes int null check (estimate_minutes between 1 and 600000),
  add column finished_before_blocker boolean not null default false;
alter table spaces.task add constraint task_dates check (start_on is null or due_on is null or start_on <= due_on);

-- A task is blocked by another task of the same space. Deleting either task removes the link.
create table spaces.task_link (
  tenant_id uuid not null,
  space_id uuid not null,
  task_id uuid not null,
  blocker_id uuid not null,
  created_by uuid not null,
  created_at timestamptz not null,
  primary key (tenant_id, task_id, blocker_id),
  check (task_id <> blocker_id),
  foreign key (tenant_id, task_id) references spaces.task (tenant_id, id) on delete cascade,
  foreign key (tenant_id, blocker_id) references spaces.task (tenant_id, id) on delete cascade
);
create index task_link_blocker on spaces.task_link (tenant_id, blocker_id);
create index task_link_space on spaces.task_link (tenant_id, space_id);
select platform.enable_tenant_rls('spaces.task_link');
grant select, insert, update, delete on spaces.task_link to tb_app;

-- Time a person spent on a task on one day (FR-021). Kept with the task; removed when the task is deleted.
create table spaces.time_entry (
  tenant_id uuid not null,
  id uuid not null,
  space_id uuid not null,
  task_id uuid not null,
  user_id uuid not null,
  worked_on date not null,
  minutes int not null check (minutes between 1 and 1440),
  note text not null default '',
  created_at timestamptz not null,
  updated_at timestamptz not null,
  primary key (tenant_id, id),
  foreign key (tenant_id, task_id) references spaces.task (tenant_id, id) on delete cascade
);
create index time_entry_task on spaces.time_entry (tenant_id, task_id, worked_on);
create index time_entry_space on spaces.time_entry (tenant_id, space_id, worked_on);
select platform.enable_tenant_rls('spaces.time_entry');
grant select, insert, update, delete on spaces.time_entry to tb_app;
