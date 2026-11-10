-- Spec 017 (R2) T001: the connector runtime. Connections of any pack, the run history that doubles as the dead letter
-- list, call attempts for the circuit breaker, and the tenant's outbound host allowlist. All tenant-owned, forced RLS.

-- Connection kinds now come from the registered packs, checked in code; Microsoft 365 keeps its external tenant ID.
alter table integrations.connection drop constraint connection_kind_check;
alter table integrations.connection alter column external_tenant_id drop not null;
alter table integrations.connection
  add column pack_version int not null default 1,
  add column rate_limit_per_minute int not null default 600 check (rate_limit_per_minute between 1 and 100000),
  add column settings jsonb not null default '{}',
  add column circuit_opened_at timestamptz null;

-- One row per connector call or sync (spec 017 US3). No payload content: the input and output stay with the caller.
-- A failed or unknown run is a dead letter while it can still be replayed (14 days); history is kept 90 days.
create table integrations.run (
  tenant_id uuid not null,
  id uuid not null,
  connection_id uuid not null,
  action text not null,
  mode text not null check (mode in ('inline', 'job')),
  idempotency_key text not null check (length(idempotency_key) between 1 and 200),
  correlation_id text null,
  status text not null check (status in ('queued', 'running', 'retrying', 'succeeded', 'business_error', 'failed', 'unknown', 'refused')),
  attempts int not null default 0,
  max_attempts int not null,
  next_attempt_at timestamptz null,
  error_class text null,
  error_code text null,
  previous_error_class text null,
  external_reference text null,
  record_count int null,
  checkpoint jsonb null,
  input jsonb null,
  duration_ms int null,
  replays int not null default 0,
  last_replayed_at timestamptz null,
  last_replayed_by uuid null,
  created_at timestamptz not null default now(),
  finished_at timestamptz null,
  primary key (tenant_id, id),
  foreign key (tenant_id, connection_id) references integrations.connection (tenant_id, id) on delete cascade
);
create unique index run_idempotency on integrations.run (tenant_id, connection_id, action, idempotency_key);
create index run_history on integrations.run (tenant_id, connection_id, created_at desc, id desc);
create index run_due on integrations.run (tenant_id, next_attempt_at) where status in ('queued', 'retrying');
select platform.enable_tenant_rls('integrations.run');
grant select, insert, update, delete on integrations.run to tb_app;

-- Every attempt, kept a day, so every instance sees the same failure ratio for the circuit breaker.
create table integrations.call_attempt (
  tenant_id uuid not null,
  connection_id uuid not null,
  at timestamptz not null,
  ok boolean not null,
  foreign key (tenant_id, connection_id) references integrations.connection (tenant_id, id) on delete cascade
);
create index call_attempt_recent on integrations.call_attempt (tenant_id, connection_id, at desc);
select platform.enable_tenant_rls('integrations.call_attempt');
grant select, insert, delete on integrations.call_attempt to tb_app;

-- Hosts outbound calls may reach (spec 017 FR-012): an exact host name or a wildcard of the form *.example.com.
create table integrations.host_allowlist (
  tenant_id uuid not null,
  host text not null check (host ~ '^(\*\.)?[a-z0-9]([a-z0-9-]*[a-z0-9])?(\.[a-z0-9]([a-z0-9-]*[a-z0-9])?)+$' and length(host) <= 253),
  added_by uuid null,
  added_at timestamptz not null default now(),
  primary key (tenant_id, host)
);
select platform.enable_tenant_rls('integrations.host_allowlist');
grant select, insert, delete on integrations.host_allowlist to tb_app;
