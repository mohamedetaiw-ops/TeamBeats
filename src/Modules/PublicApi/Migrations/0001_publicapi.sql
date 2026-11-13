-- Spec 035 (R2) phase 1: app registrations, their credentials, consents, token families and the tenant's API settings.
-- Every table is tenant-owned with forced row-level security (ADR-0018), so an app ID, a consent or a token family of
-- tenant B is never found under tenant A. No token and no secret is stored in clear: client secrets are salted hashes
-- and a refresh token is known only by the hash of its current generation.
create schema publicapi;
grant usage on schema publicapi to tb_app;

-- The tenant's API settings (section 6): token lifetimes the tenant may only lower, the scopes employees may consent to
-- themselves (null means the catalogue default, the read scopes) and the tenant switch for the public API.
create table publicapi.setting (
  tenant_id uuid not null primary key,
  access_token_minutes int not null default 10 check (access_token_minutes between 5 and 60),
  refresh_token_days int not null default 30 check (refresh_token_days between 1 and 30),
  user_consent_scopes text[] null,
  api_enabled boolean not null default true,
  version int not null default 1,
  updated_by uuid null,
  updated_at timestamptz not null default now()
);
select platform.enable_tenant_rls('publicapi.setting');
grant select, insert, update on publicapi.setting to tb_app;

-- An app registration (FR-004): an application that acts as itself, a delegated web or public (native or single page)
-- app that acts for an employee, or an MCP client. client_id is the public identifier the app presents; redirect URIs
-- match exactly. tokens_revoked_at refuses every token issued before it (emergency revoke, FR-028).
create table publicapi.app (
  tenant_id uuid not null,
  id uuid not null,
  client_id text not null,
  kind text not null check (kind in ('application', 'delegated_web', 'delegated_public', 'mcp_client')),
  name text not null check (length(name) between 1 and 80),
  description text null check (length(description) <= 500),
  publisher text null check (length(publisher) <= 80),
  scopes text[] not null,
  redirect_uris text[] not null default '{}',
  allow_guests boolean not null default false,
  status text not null default 'active' check (status in ('active', 'disabled')),
  tokens_revoked_at timestamptz null,
  last_used_at timestamptz null,
  version int not null default 1,
  created_by uuid not null,
  created_at timestamptz not null default now(),
  updated_at timestamptz not null default now(),
  primary key (tenant_id, id),
  unique (tenant_id, client_id)
);
select platform.enable_tenant_rls('publicapi.app');
grant select, insert, update on publicapi.app to tb_app;

-- A credential of a confidential app (FR-005): a client secret kept as a salted hash and shown once, or later a public
-- key for private key JWT. Up to two are active at a time, for rotation.
create table publicapi.app_credential (
  tenant_id uuid not null,
  id uuid not null,
  app_id uuid not null,
  kind text not null check (kind in ('secret', 'public_key')),
  secret_hash text null,
  hint text null,
  expires_at timestamptz not null,
  last_used_at timestamptz null,
  created_by uuid not null,
  created_at timestamptz not null default now(),
  revoked_at timestamptz null,
  revoked_by uuid null,
  primary key (tenant_id, id),
  foreign key (tenant_id, app_id) references publicapi.app (tenant_id, id)
);
select platform.enable_tenant_rls('publicapi.app_credential');
grant select, insert, update on publicapi.app_credential to tb_app;
create index app_credential_app on publicapi.app_credential (tenant_id, app_id);

-- A consent (FR-013): an employee's own (user_id set) or an API administrator's for the whole tenant (user_id null).
-- One live consent per app and person; granting more scopes widens it, revoking ends it and its token families.
create table publicapi.consent (
  tenant_id uuid not null,
  id uuid not null,
  app_id uuid not null,
  user_id uuid null,
  scopes text[] not null,
  granted_by uuid not null,
  granted_at timestamptz not null default now(),
  last_used_at timestamptz null,
  revoked_at timestamptz null,
  revoked_by uuid null,
  primary key (tenant_id, id),
  foreign key (tenant_id, app_id) references publicapi.app (tenant_id, id)
);
select platform.enable_tenant_rls('publicapi.consent');
grant select, insert, update on publicapi.consent to tb_app;
create unique index consent_live_user on publicapi.consent (tenant_id, app_id, user_id) where revoked_at is null and user_id is not null;
create unique index consent_live_admin on publicapi.consent (tenant_id, app_id) where revoked_at is null and user_id is null;

-- A token family (FR-006): one sign-in of a person to an app, from its authorization code through every refresh.
-- The code is redeemed once; each refresh replaces the generation, so presenting an older refresh token is reuse and
-- revokes the family. Application tokens use their credential as the family and have no row here.
create table publicapi.token_family (
  tenant_id uuid not null,
  id uuid not null,
  app_id uuid not null,
  user_id uuid not null,
  scopes text[] not null,
  resource text not null,
  code_redeemed_at timestamptz null,
  generation_hash text null,
  created_at timestamptz not null default now(),
  last_used_at timestamptz null,
  expires_at timestamptz not null,
  revoked_at timestamptz null,
  revoked_reason text null check (revoked_reason in ('user', 'admin', 'reuse_detected', 'consent_revoked', 'app_disabled', 'membership_ended')),
  primary key (tenant_id, id),
  foreign key (tenant_id, app_id) references publicapi.app (tenant_id, id)
);
select platform.enable_tenant_rls('publicapi.token_family');
grant select, insert, update on publicapi.token_family to tb_app;
create index token_family_app_user on publicapi.token_family (tenant_id, app_id, user_id) where revoked_at is null;
