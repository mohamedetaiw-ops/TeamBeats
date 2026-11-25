-- Spec 026 US8, FR-007 (R3): customer-managed keys. One key connection per tenant: where the key lives, the tenant's
-- data key wrapped with it, and what the last check found. The plaintext data key is never stored.
create table security.tenant_key (
  tenant_id uuid primary key,
  provider text not null check (provider in ('azure_key_vault', 'vault_transit')),
  key_uri text not null,
  directory_id text null,
  client_id text null,
  secret_ref text not null,
  data_key_id uuid not null,
  key_version text not null,
  wrapped_data_key bytea not null,
  status text not null check (status in ('active', 'unreachable', 'revoked')),
  status_since timestamptz not null,
  checked_at timestamptz not null,
  last_ok_at timestamptz not null,
  last_error text null,
  connected_at timestamptz not null,
  connected_by uuid null,
  rotated_at timestamptz null
);
select platform.enable_tenant_rls('security.tenant_key');
grant select, insert, update on security.tenant_key to tb_app;
