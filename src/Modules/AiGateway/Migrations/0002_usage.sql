-- Spec 013 US4 and US8 scenario 1 (R1): one call record per gateway request with metadata only, never prompt or
-- response text. Written in its own transaction as the call ends, so a caller's rollback or a worker restart loses no
-- record. Kept 25 months, then aggregated (retention job in R2).
create table ai.usage_record (
  tenant_id uuid not null,
  id uuid not null,
  at timestamptz not null,
  unit_id uuid null,
  user_id uuid null,
  use_case text not null,
  agent_id uuid null,
  agent_version int null,
  model text null,
  model_requested text null,
  provider text null,
  input_tokens int not null default 0,
  output_tokens int not null default 0,
  latency_ms int not null default 0,
  cost numeric(14, 6) not null default 0,
  decision text not null check (decision in ('served', 'denied')),
  denial text null,
  classification text null,
  fallback boolean not null default false,
  partial boolean not null default false,
  charged_to text not null default 'tenant' check (charged_to in ('tenant', 'platform')),
  correlation_id text null,
  primary key (tenant_id, id)
);
create index usage_record_at on ai.usage_record (tenant_id, at);
select platform.enable_tenant_rls('ai.usage_record');
grant select, insert on ai.usage_record to tb_app;
