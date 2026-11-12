-- Spec 029 US6 and FR-009: events on a space's team calendar. The calendar also shows milestones, task due dates and
-- the members' approved leave, which are read from their owners and never copied here.
create table spaces.space_event (
  tenant_id uuid not null,
  id uuid not null,
  space_id uuid not null,
  title text not null,
  description text not null default '',
  starts_at timestamptz not null,
  ends_at timestamptz not null,
  all_day boolean not null default false,
  created_by uuid not null,
  created_at timestamptz not null,
  updated_at timestamptz not null,
  row_version int not null default 1,
  primary key (tenant_id, id),
  foreign key (tenant_id, space_id) references spaces.space (tenant_id, id),
  check (ends_at >= starts_at)
);
create index space_event_space on spaces.space_event (tenant_id, space_id, starts_at);
select platform.enable_tenant_rls('spaces.space_event');
grant select, insert, update, delete on spaces.space_event to tb_app;
