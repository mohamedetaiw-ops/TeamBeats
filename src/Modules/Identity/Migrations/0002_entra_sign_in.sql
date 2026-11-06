-- Spec 003 US2 (R1): Entra ID sign-in over OIDC authorization code flow with PKCE.

-- One enabled OIDC provider per tenant in R1; the secret stays a secret-manager reference (FR-002).
alter table identity.idp_config
  add column enabled boolean not null default true,
  add column display_name text null,
  add column updated_at timestamptz not null default now();
create unique index idp_config_one_enabled on identity.idp_config (tenant_id) where enabled;

-- Pending sign-ins. The state and the browser binding are stored only as hashes. A row is consumed once, so a
-- replayed callback, a replayed authorization code or a replayed ID token (nonce) is refused.
create table identity.oidc_login_state (
  tenant_id uuid not null,
  id uuid not null,
  state_hash text not null,
  browser_binding_hash text not null,
  idp_config_id uuid not null,
  nonce text not null,
  code_verifier text not null,
  return_path text not null,
  created_at timestamptz not null default now(),
  expires_at timestamptz not null,
  consumed_at timestamptz null,
  primary key (tenant_id, id),
  unique (tenant_id, state_hash),
  foreign key (tenant_id, idp_config_id) references identity.idp_config (tenant_id, id)
);
select platform.enable_tenant_rls('identity.oidc_login_state');
grant select, insert, update, delete on identity.oidc_login_state to tb_app;

-- Role assignments that come from the identity provider's group claim (US2 scenario 6). Tracked here so that
-- leaving the group at the identity provider removes the role at the next sign-in, and admin-made assignments are
-- never touched.
create table identity.group_role_grant (
  tenant_id uuid not null,
  user_id uuid not null,
  group_id text not null,
  role_key text not null,
  scope_type text not null,
  scope_id uuid null,
  assignment_id uuid not null,
  created_at timestamptz not null default now(),
  primary key (tenant_id, assignment_id),
  foreign key (tenant_id, user_id) references identity.user_account (tenant_id, id)
);
create index group_role_grant_user on identity.group_role_grant (tenant_id, user_id);
select platform.enable_tenant_rls('identity.group_role_grant');
grant select, insert, delete on identity.group_role_grant to tb_app;
