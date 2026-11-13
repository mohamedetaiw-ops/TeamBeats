-- Spec 026 US4 (R2): SIEM destinations and their place in the tenant's audit stream. The connection (URL or host,
-- secret reference) lives in Integrations; this row keeps the cursor and the delivery state.
create table security.siem_destination (
  tenant_id uuid not null,
  id uuid not null,
  name text not null check (length(name) between 1 and 120),
  transport text not null check (transport in ('https', 'syslog')),
  target text not null check (length(target) <= 2100),
  secret_ref text null check (length(secret_ref) <= 200),
  connection_id uuid not null,
  enabled boolean not null default true,
  last_position bigint not null default 0,
  last_success_at timestamptz null,
  failing_since timestamptz null,
  last_error text null check (length(last_error) <= 100),
  failures int not null default 0,
  next_attempt_at timestamptz null,
  failure_audited_at timestamptz null,
  alerted_at timestamptz null,
  version int not null default 1,
  created_at timestamptz not null default now(),
  updated_at timestamptz not null default now(),
  primary key (tenant_id, id)
);
select platform.enable_tenant_rls('security.siem_destination');
grant select, insert, update, delete on security.siem_destination to tb_app;
