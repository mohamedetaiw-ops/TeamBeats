-- Spec 029 slice 2 (T004 to T007): task details, milestones and the space's status history. Every table is
-- tenant-owned with forced row-level security, like the rest of the schema.

-- A milestone (FR-005). Progress comes from the tasks linked to it; reached_at is set once every linked task is done.
create table spaces.milestone (
  tenant_id uuid not null,
  id uuid not null,
  space_id uuid not null,
  name text not null,
  due_on date null,
  position int not null,
  reached_at timestamptz null,
  created_by uuid not null,
  created_at timestamptz not null,
  row_version int not null default 1,
  primary key (tenant_id, id),
  foreign key (tenant_id, space_id) references spaces.space (tenant_id, id)
);
create index milestone_space on spaces.milestone (tenant_id, space_id, position);
select platform.enable_tenant_rls('spaces.milestone');
grant select, insert, update, delete on spaces.milestone to tb_app;

-- A task belongs to at most one milestone. reminded_for is the due date its day-before reminder was sent for.
alter table spaces.task add column milestone_id uuid null, add column reminded_for date null;
alter table spaces.task add foreign key (tenant_id, milestone_id) references spaces.milestone (tenant_id, id);
create index task_milestone on spaces.task (tenant_id, milestone_id) where milestone_id is not null;
create index task_due on spaces.task (tenant_id, due_on) where status = 'open' and due_on is not null;

create table spaces.task_assignee (
  tenant_id uuid not null,
  task_id uuid not null,
  user_id uuid not null,
  assigned_by uuid not null,
  assigned_at timestamptz not null,
  primary key (tenant_id, task_id, user_id),
  foreign key (tenant_id, task_id) references spaces.task (tenant_id, id) on delete cascade
);
create index task_assignee_user on spaces.task_assignee (tenant_id, user_id);
select platform.enable_tenant_rls('spaces.task_assignee');
grant select, insert, update, delete on spaces.task_assignee to tb_app;

create table spaces.checklist_item (
  tenant_id uuid not null,
  id uuid not null,
  task_id uuid not null,
  text text not null,
  done boolean not null default false,
  position int not null,
  primary key (tenant_id, id),
  foreign key (tenant_id, task_id) references spaces.task (tenant_id, id) on delete cascade
);
create index checklist_item_task on spaces.checklist_item (tenant_id, task_id, position);
select platform.enable_tenant_rls('spaces.checklist_item');
grant select, insert, update, delete on spaces.checklist_item to tb_app;

create table spaces.task_comment (
  tenant_id uuid not null,
  id uuid not null,
  task_id uuid not null,
  author_id uuid not null,
  body text not null,
  created_at timestamptz not null,
  primary key (tenant_id, id),
  foreign key (tenant_id, task_id) references spaces.task (tenant_id, id) on delete cascade
);
create index task_comment_task on spaces.task_comment (tenant_id, task_id, created_at);
select platform.enable_tenant_rls('spaces.task_comment');
grant select, insert, update, delete on spaces.task_comment to tb_app;

-- The owner's status of the space with its note (FR-005). The newest entry is the current status.
create table spaces.status_entry (
  tenant_id uuid not null,
  id uuid not null,
  space_id uuid not null,
  status text not null check (status in ('on_track', 'at_risk', 'off_track', 'done')),
  note text not null default '',
  set_by uuid not null,
  set_at timestamptz not null,
  primary key (tenant_id, id),
  foreign key (tenant_id, space_id) references spaces.space (tenant_id, id)
);
create index status_entry_space on spaces.status_entry (tenant_id, space_id, set_at desc);
select platform.enable_tenant_rls('spaces.status_entry');
grant select, insert, update, delete on spaces.status_entry to tb_app;
