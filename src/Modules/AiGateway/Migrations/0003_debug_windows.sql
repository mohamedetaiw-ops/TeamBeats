-- Spec 013 US8 (R1): time-limited debug windows per use case. While a window is open, full prompts and answers of
-- Public and Internal calls are stored encrypted with the installation's AI log key, readable by security officers,
-- and deleted after 30 days. Restricted and Confidential text is never stored.
create table ai.debug_window (
  tenant_id uuid not null,
  id uuid not null,
  use_case text not null,
  opened_by uuid not null,
  opened_at timestamptz not null,
  ends_at timestamptz not null,
  closed_at timestamptz null,
  closed_by uuid null,
  primary key (tenant_id, id)
);
select platform.enable_tenant_rls('ai.debug_window');
grant select, insert, update on ai.debug_window to tb_app;

create table ai.debug_log (
  tenant_id uuid not null,
  id uuid not null,
  window_id uuid not null,
  at timestamptz not null,
  use_case text not null,
  classification text not null check (classification in ('public', 'internal')),
  model text not null,
  nonce bytea not null,
  ciphertext bytea not null,
  correlation_id text null,
  primary key (tenant_id, id),
  foreign key (tenant_id, window_id) references ai.debug_window (tenant_id, id)
);
create index debug_log_at on ai.debug_log (tenant_id, at);
select platform.enable_tenant_rls('ai.debug_log');
grant select, insert, delete on ai.debug_log to tb_app;
