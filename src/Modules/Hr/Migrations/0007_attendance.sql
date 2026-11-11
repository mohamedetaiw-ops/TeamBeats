-- Spec 022 slice 4 (T020 part 1): attendance records. Sign in and out on the web with an optional location check,
-- clock data from spec 017, working hours rules, and the days and monthly summaries computed from them.

-- Tenant setting, versioned. Sites are where sign in counts as on site; only the remote flag is kept per punch, never
-- the coordinates.
create table hr.attendance_settings (
  tenant_id uuid not null primary key,
  web_sign_in boolean not null default false,
  location_mode text not null default 'off' check (location_mode in ('off', 'flag', 'refuse')),
  sites jsonb not null default '[]',
  version int not null default 1,
  updated_by uuid null,
  updated_at timestamptz null
);
select platform.enable_tenant_rls('hr.attendance_settings');
grant select, insert, update on hr.attendance_settings to tb_app;

-- Working hours rules: one for the whole tenant (org_unit_id null) and at most one per unit; the nearest applies.
create table hr.work_rule (
  tenant_id uuid not null,
  id uuid not null,
  org_unit_id uuid null,
  start_time time not null,
  end_time time not null,
  grace_late_minutes int not null check (grace_late_minutes between 0 and 120),
  grace_early_minutes int not null check (grace_early_minutes between 0 and 120),
  version int not null default 1,
  updated_by uuid null,
  updated_at timestamptz not null,
  primary key (tenant_id, id),
  check (end_time > start_time)
);
create unique index work_rule_unit on hr.work_rule (tenant_id, coalesce(org_unit_id, '00000000-0000-0000-0000-000000000000'::uuid));
select platform.enable_tenant_rls('hr.work_rule');
grant select, insert, update, delete on hr.work_rule to tb_app;

create table hr.attendance_punch (
  tenant_id uuid not null,
  id uuid not null,
  user_id uuid not null,
  at timestamptz not null,
  direction text not null check (direction in ('in', 'out')),
  source text not null check (source in ('web', 'clock')),
  remote boolean not null default false,
  connection_id uuid null,
  external_id text null,
  created_at timestamptz not null,
  primary key (tenant_id, id),
  check ((source = 'clock') = (connection_id is not null and external_id is not null))
);
create index attendance_punch_user on hr.attendance_punch (tenant_id, user_id, at);
create unique index attendance_punch_clock on hr.attendance_punch (tenant_id, connection_id, external_id) where source = 'clock';
select platform.enable_tenant_rls('hr.attendance_punch');
grant select, insert on hr.attendance_punch to tb_app;

-- How far clock data has been read from Integrations (IClockData sequence).
create table hr.attendance_import (
  tenant_id uuid not null primary key,
  last_sequence bigint not null default 0,
  updated_at timestamptz not null
);
select platform.enable_tenant_rls('hr.attendance_import');
grant select, insert, update on hr.attendance_import to tb_app;
