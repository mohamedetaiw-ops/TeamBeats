-- Spec 017 US7 scenario 2 (FR-021): approved claims handed to the finance or ERP system. One row per claim of the
-- calling module, with the finance system's reference and the payment status read back. No claim content is kept
-- here; the connector run holds the input only while it may still run. Tenant-owned, forced RLS.
create table integrations.finance_handoff (
  tenant_id uuid not null,
  id uuid not null,
  connection_id uuid not null,
  source_type text not null check (source_type ~ '^[a-z_]{1,50}$'),
  source_id uuid not null,
  state text not null check (state in ('queued', 'sent', 'failed', 'unknown')),
  attempt int not null default 1,
  external_reference text null check (external_reference is null or length(external_reference) <= 200),
  payment_status text null check (payment_status in ('submitted', 'approved', 'paid', 'rejected')),
  error_class text null,
  error_code text null,
  created_at timestamptz not null,
  sent_at timestamptz null,
  checked_at timestamptz null,
  next_check_at timestamptz null,
  primary key (tenant_id, id),
  foreign key (tenant_id, connection_id) references integrations.connection (tenant_id, id) on delete cascade
);
create unique index finance_handoff_source on integrations.finance_handoff (tenant_id, source_type, source_id);
create index finance_handoff_due on integrations.finance_handoff (tenant_id, next_check_at) where state = 'sent';
create index finance_handoff_connection on integrations.finance_handoff (tenant_id, connection_id, created_at desc);
select platform.enable_tenant_rls('integrations.finance_handoff');
grant select, insert, update, delete on integrations.finance_handoff to tb_app;
