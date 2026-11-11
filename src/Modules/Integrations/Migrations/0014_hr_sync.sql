-- Spec 019 T001: HR sync. A tenant has at most one HR connection; its employees are kept in canonical form with the
-- match to a TeamBeats user, each run is recorded with its counts, and a run held by the change threshold keeps its
-- incoming records until an HR administrator confirms it or a later run replaces it. Tenant-owned, forced RLS.
create table integrations.hr_settings (
  tenant_id uuid primary key,
  match_rule text[] not null default '{work_email,employee_number}'
    check (match_rule <@ array['work_email', 'employee_number']::text[] and cardinality(match_rule) between 1 and 2),
  change_threshold_percent int not null default 10 check (change_threshold_percent between 1 and 100),
  version int not null default 1,
  updated_at timestamptz not null default now()
);
select platform.enable_tenant_rls('integrations.hr_settings');
grant select, insert, update, delete on integrations.hr_settings to tb_app;

create table integrations.hr_run (
  tenant_id uuid not null,
  id uuid not null,
  connection_id uuid not null,
  started_at timestamptz not null,
  finished_at timestamptz,
  status text not null check (status in ('applied', 'held', 'confirmed', 'discarded', 'superseded')),
  total int not null default 0,
  matched int not null default 0,
  unmatched int not null default 0,
  conflicts int not null default 0,
  refused int not null default 0,
  changed int not null default 0,
  terminations int not null default 0,
  moves int not null default 0,
  active_before int not null default 0,
  problems jsonb not null default '[]',
  decided_by uuid,
  decided_at timestamptz,
  primary key (tenant_id, id),
  foreign key (tenant_id, connection_id) references integrations.connection (tenant_id, id) on delete cascade
);
create index hr_run_started on integrations.hr_run (tenant_id, started_at desc);
select platform.enable_tenant_rls('integrations.hr_run');
grant select, insert, update, delete on integrations.hr_run to tb_app;

-- The last applied copy of each HR employee (FR-018: last_run_id and record_id trace every change) and its match.
create table integrations.hr_employee (
  tenant_id uuid not null,
  record_id text not null check (length(record_id) between 1 and 100),
  connection_id uuid not null,
  data jsonb not null,
  user_id uuid,
  match_state text not null check (match_state in ('matched', 'unmatched', 'conflict')),
  match_rule text check (match_rule in ('work_email', 'employee_number', 'manual')),
  linked_by uuid,
  first_seen_at timestamptz not null,
  last_seen_at timestamptz not null,
  last_run_id uuid not null,
  primary key (tenant_id, record_id),
  foreign key (tenant_id, connection_id) references integrations.connection (tenant_id, id) on delete cascade
);
create unique index hr_employee_user on integrations.hr_employee (tenant_id, user_id) where user_id is not null;
create index hr_employee_state on integrations.hr_employee (tenant_id, match_state, record_id);
select platform.enable_tenant_rls('integrations.hr_employee');
grant select, insert, update, delete on integrations.hr_employee to tb_app;

-- Records of a held run, until it is confirmed or replaced.
create table integrations.hr_incoming (
  tenant_id uuid not null,
  run_id uuid not null,
  record_id text not null,
  data jsonb not null,
  primary key (tenant_id, run_id, record_id),
  foreign key (tenant_id, run_id) references integrations.hr_run (tenant_id, id) on delete cascade
);
select platform.enable_tenant_rls('integrations.hr_incoming');
grant select, insert, update, delete on integrations.hr_incoming to tb_app;

-- The nightly full sync: the last local date it was queued for each connection.
create table integrations.hr_schedule (
  tenant_id uuid not null,
  connection_id uuid not null,
  last_date date not null,
  primary key (tenant_id, connection_id),
  foreign key (tenant_id, connection_id) references integrations.connection (tenant_id, id) on delete cascade
);
select platform.enable_tenant_rls('integrations.hr_schedule');
grant select, insert, update, delete on integrations.hr_schedule to tb_app;
