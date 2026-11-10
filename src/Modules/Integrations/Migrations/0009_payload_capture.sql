-- Spec 017 FR-007: payload capture for debugging, off by default. An administrator turns it on for one connection for
-- at most 24 hours; while it is on, each finished run keeps its input and output for 24 hours, then they are purged.
-- Tenant-owned, forced RLS.
alter table integrations.connection add column capture_until timestamptz null;

create table integrations.run_capture (
  tenant_id uuid not null,
  run_id uuid not null,
  connection_id uuid not null,
  input jsonb null,
  output jsonb null,
  captured_at timestamptz not null,
  expires_at timestamptz not null,
  primary key (tenant_id, run_id),
  foreign key (tenant_id, run_id) references integrations.run (tenant_id, id) on delete cascade
);
create index run_capture_expiry on integrations.run_capture (tenant_id, expires_at);
select platform.enable_tenant_rls('integrations.run_capture');
grant select, insert, update, delete on integrations.run_capture to tb_app;
