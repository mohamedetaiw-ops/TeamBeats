-- Spec 017 US7 (FR-020): clock data imported over SFTP or from an uploaded CSV file. Each file is recorded once, its
-- rows become canonical punches (one per source punch ID), and rows that could not be imported are listed for the
-- administrator. Tenant-owned, forced RLS.

-- One file per source fingerprint (name, size and change time over SFTP; content hash for an upload). An upload
-- keeps its content until it is imported.
create table integrations.clock_file (
  tenant_id uuid not null,
  id uuid not null,
  connection_id uuid not null,
  source text not null check (source in ('sftp', 'upload')),
  name text not null check (length(name) between 1 and 255),
  fingerprint text not null check (length(fingerprint) between 1 and 400),
  size_bytes bigint not null,
  status text not null check (status in ('pending', 'imported', 'refused')),
  content bytea null,
  error_key text null,
  row_count int not null default 0,
  imported int not null default 0,
  duplicates int not null default 0,
  unmatched int not null default 0,
  first_at timestamptz null,
  last_at timestamptz null,
  received_by uuid null,
  received_at timestamptz not null,
  imported_at timestamptz null,
  primary key (tenant_id, id),
  foreign key (tenant_id, connection_id) references integrations.connection (tenant_id, id) on delete cascade
);
create unique index clock_file_fingerprint on integrations.clock_file (tenant_id, connection_id, fingerprint);
create index clock_file_recent on integrations.clock_file (tenant_id, connection_id, received_at desc, id desc);
create index clock_file_pending on integrations.clock_file (tenant_id, connection_id) where status = 'pending';
select platform.enable_tenant_rls('integrations.clock_file');
grant select, insert, update, delete on integrations.clock_file to tb_app;

-- Canonical clock events for spec 022, read in sequence order through IClockData.
create table integrations.clock_punch (
  tenant_id uuid not null,
  seq bigint generated always as identity,
  connection_id uuid not null,
  punch_id text not null check (length(punch_id) between 1 and 200),
  user_id uuid not null,
  at timestamptz not null,
  direction text not null check (direction in ('in', 'out', 'unknown')),
  device text null check (device is null or length(device) <= 200),
  file_id uuid not null,
  imported_at timestamptz not null,
  primary key (tenant_id, seq),
  foreign key (tenant_id, connection_id) references integrations.connection (tenant_id, id) on delete cascade
);
create unique index clock_punch_source on integrations.clock_punch (tenant_id, connection_id, punch_id);
create index clock_punch_user on integrations.clock_punch (tenant_id, user_id, at);
select platform.enable_tenant_rls('integrations.clock_punch');
grant select, insert, delete on integrations.clock_punch to tb_app;

-- Rows not imported: line number, the employee number as written, and why. No other row content is kept.
create table integrations.clock_problem (
  tenant_id uuid not null,
  file_id uuid not null,
  line int not null,
  employee_number text null,
  reason text not null check (reason in ('unknown_employee', 'missing_employee', 'bad_time', 'bad_direction', 'too_long')),
  primary key (tenant_id, file_id, line),
  foreign key (tenant_id, file_id) references integrations.clock_file (tenant_id, id) on delete cascade
);
select platform.enable_tenant_rls('integrations.clock_problem');
grant select, insert, delete on integrations.clock_problem to tb_app;

-- The last day the daily SFTP import was queued for, per connection, so it is queued once a day.
create table integrations.clock_schedule (
  tenant_id uuid not null,
  connection_id uuid not null,
  last_date date not null,
  primary key (tenant_id, connection_id),
  foreign key (tenant_id, connection_id) references integrations.connection (tenant_id, id) on delete cascade
);
select platform.enable_tenant_rls('integrations.clock_schedule');
grant select, insert, update, delete on integrations.clock_schedule to tb_app;
