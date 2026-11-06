-- Spec 018 (R1): delegated Microsoft Graph access as the signed-in employee, and per-feature options.
alter table integrations.connection_feature add column options jsonb not null default '{}';

-- One encrypted access token per tenant membership and session (spec 003 FR-016). The ciphertext is bound to tenant,
-- user and session by AES-GCM associated data, so a copied row cannot be decrypted elsewhere. Never logged or returned.
create table integrations.delegated_token (
  tenant_id uuid not null,
  user_id uuid not null,
  session_id uuid not null,
  connection_id uuid not null,
  scopes text[] not null,
  nonce bytea not null,
  ciphertext bytea not null,
  expires_at timestamptz not null,
  created_at timestamptz not null default now(),
  primary key (tenant_id, user_id, session_id),
  foreign key (tenant_id, connection_id) references integrations.connection (tenant_id, id) on delete cascade
);
create index delegated_token_user on integrations.delegated_token (tenant_id, user_id);
select platform.enable_tenant_rls('integrations.delegated_token');
grant select, insert, update, delete on integrations.delegated_token to tb_app;

-- Pending "Connect Outlook" requests: state and browser binding as hashes, single use, bound to the session.
create table integrations.outlook_connect_request (
  tenant_id uuid not null,
  id uuid not null,
  user_id uuid not null,
  session_id uuid not null,
  connection_id uuid not null,
  state_hash text not null,
  browser_binding_hash text not null,
  code_verifier text not null,
  scopes text[] not null,
  return_to text not null,
  created_at timestamptz not null default now(),
  expires_at timestamptz not null,
  consumed_at timestamptz null,
  -- Why Microsoft refused (admin_setup, conditional_access), so the status does not invite a consent loop (FR-004).
  outcome text null,
  primary key (tenant_id, id),
  unique (tenant_id, state_hash),
  foreign key (tenant_id, connection_id) references integrations.connection (tenant_id, id) on delete cascade
);
select platform.enable_tenant_rls('integrations.outlook_connect_request');
grant select, insert, update, delete on integrations.outlook_connect_request to tb_app;
