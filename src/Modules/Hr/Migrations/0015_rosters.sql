-- Spec 022 R3 slice 9 (US10, FR-029): shift rosters. Shift patterns are tenant configuration kept by HR officers of the
-- whole organization; a roster week is one manager's plan for their direct reports. Only published weeks count for
-- attendance; the published copy is kept so a later change tells only the people whose days changed.
create table hr.shift_pattern (
  tenant_id uuid not null,
  id uuid not null,
  names jsonb not null,
  start_time time not null,
  end_time time not null,
  break_minutes int not null check (break_minutes between 0 and 240),
  tone text not null check (tone in ('neutral', 'info', 'success', 'warning', 'danger')),
  active boolean not null default true,
  version int not null default 1,
  updated_by uuid null,
  updated_at timestamptz not null,
  primary key (tenant_id, id),
  check (end_time > start_time)
);
select platform.enable_tenant_rls('hr.shift_pattern');
grant select, insert, update on hr.shift_pattern to tb_app;

create table hr.roster_week (
  tenant_id uuid not null,
  id uuid not null,
  manager_id uuid not null,
  week_start date not null,
  status text not null check (status in ('draft', 'published')),
  version int not null default 1,
  -- {"<user>": {"<date>": "<pattern id>" | "off"}} as last published.
  published jsonb null,
  published_at timestamptz null,
  updated_by uuid null,
  updated_at timestamptz not null,
  primary key (tenant_id, id),
  unique (tenant_id, manager_id, week_start)
);
select platform.enable_tenant_rls('hr.roster_week');
grant select, insert, update on hr.roster_week to tb_app;

create table hr.roster_entry (
  tenant_id uuid not null,
  week_id uuid not null,
  user_id uuid not null,
  date date not null,
  pattern_id uuid null,
  day_off boolean not null,
  primary key (tenant_id, week_id, user_id, date),
  check (day_off = (pattern_id is null))
);
create index roster_entry_user on hr.roster_entry (tenant_id, user_id, date);
select platform.enable_tenant_rls('hr.roster_entry');
grant select, insert, update, delete on hr.roster_entry to tb_app;
