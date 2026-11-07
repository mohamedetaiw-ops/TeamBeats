-- Spec 022 (R1, User Story 2): working calendars, their holidays, and the org units they apply to. Every table is
-- tenant-owned with forced row-level security. A calendar owned by an org unit is managed by HR officers of that
-- unit; a calendar without an owner is a tenant calendar.
create schema hr;
grant usage on schema hr to tb_app;

create table hr.working_calendar (
  tenant_id uuid not null,
  id uuid not null,
  name jsonb not null,
  country text null check (country ~ '^[A-Z]{2}$'),
  week_days smallint[] not null check (cardinality(week_days) between 1 and 7 and week_days <@ array[1,2,3,4,5,6,7]::smallint[]),
  owner_org_unit_id uuid null,
  is_tenant_default boolean not null default false,
  version int not null default 1,
  created_at timestamptz not null,
  updated_at timestamptz not null,
  updated_by uuid null,
  primary key (tenant_id, id),
  check (not is_tenant_default or owner_org_unit_id is null)
);
create unique index working_calendar_one_default on hr.working_calendar (tenant_id) where is_tenant_default;
select platform.enable_tenant_rls('hr.working_calendar');
grant select, insert, update, delete on hr.working_calendar to tb_app;

create table hr.calendar_holiday (
  tenant_id uuid not null,
  calendar_id uuid not null,
  date date not null,
  name jsonb not null,
  half_day boolean not null default false,
  primary key (tenant_id, calendar_id, date),
  foreign key (tenant_id, calendar_id) references hr.working_calendar (tenant_id, id) on delete cascade
);
select platform.enable_tenant_rls('hr.calendar_holiday');
grant select, insert, update, delete on hr.calendar_holiday to tb_app;

create table hr.calendar_assignment (
  tenant_id uuid not null,
  org_unit_id uuid not null,
  calendar_id uuid not null,
  assigned_at timestamptz not null,
  assigned_by uuid null,
  primary key (tenant_id, org_unit_id),
  foreign key (tenant_id, calendar_id) references hr.working_calendar (tenant_id, id)
);
create index calendar_assignment_calendar on hr.calendar_assignment (tenant_id, calendar_id);
select platform.enable_tenant_rls('hr.calendar_assignment');
grant select, insert, update, delete on hr.calendar_assignment to tb_app;
