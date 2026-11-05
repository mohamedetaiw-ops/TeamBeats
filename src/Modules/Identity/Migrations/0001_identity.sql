-- Spec 003 and ADR-0005: tenant-scoped identities, no global user table. ADR-0007: PostgreSQL session store.
-- No password or local credential table exists in any migration (ADR-0004).
create schema identity;
grant usage on schema identity to tb_app;

create table identity.user_account (
  tenant_id uuid not null,
  id uuid not null,
  display_name text not null,
  email text not null,
  status text not null check (status in ('active', 'disabled')),
  preferred_locale text null check (preferred_locale is null or preferred_locale in ('ar', 'en', 'fr', 'ur', 'hi')),
  created_via text not null check (created_via in ('jit', 'admin', 'scim', 'dev')),
  created_at timestamptz not null default now(),
  primary key (tenant_id, id)
);
create unique index user_account_email on identity.user_account (tenant_id, lower(email));
select platform.enable_tenant_rls('identity.user_account');
grant select, insert, update on identity.user_account to tb_app;

-- Identity is keyed by tenant, issuer and subject, never by email (FR-003).
create table identity.external_identity (
  tenant_id uuid not null,
  id uuid not null,
  idp_config_id uuid null,
  issuer text not null,
  subject text not null,
  user_id uuid not null,
  created_at timestamptz not null default now(),
  primary key (tenant_id, id),
  unique (tenant_id, issuer, subject),
  foreign key (tenant_id, user_id) references identity.user_account (tenant_id, id)
);
select platform.enable_tenant_rls('identity.external_identity');
grant select, insert on identity.external_identity to tb_app;

-- Per-tenant IdP configuration (FR-002). The client secret is a secret-manager reference only.
create table identity.idp_config (
  tenant_id uuid not null,
  id uuid not null,
  protocol text not null check (protocol in ('oidc', 'saml')),
  kind text not null check (kind in ('entra', 'google', 'generic')),
  issuer text not null,
  client_id text not null,
  secret_ref text not null,
  jit_enabled boolean not null default false,
  jit_domains text[] not null default '{}',
  group_role_map jsonb not null default '[]',
  created_at timestamptz not null default now(),
  primary key (tenant_id, id)
);
select platform.enable_tenant_rls('identity.idp_config');
grant select, insert, update on identity.idp_config to tb_app;

-- One tenant per verified domain per installation (spec 003 section 6). Platform table, RLS exception.
create table identity.verified_domain (
  domain text primary key check (domain = lower(domain)),
  tenant_id uuid not null,
  verified_at timestamptz not null default now()
);
grant select on identity.verified_domain to tb_app;

-- Sessions: the cookie holds an opaque token; only its SHA-256 hash is stored.
create table identity.user_session (
  tenant_id uuid not null,
  id uuid not null,
  user_id uuid not null,
  token_hash text not null unique,
  method text not null,
  created_at timestamptz not null default now(),
  expires_at timestamptz not null,
  revoked_at timestamptz null,
  revoked_reason text null,
  primary key (tenant_id, id),
  foreign key (tenant_id, user_id) references identity.user_account (tenant_id, id)
);
create index user_session_user on identity.user_session (tenant_id, user_id) where revoked_at is null;
select platform.enable_tenant_rls('identity.user_session');
grant select, insert, update on identity.user_session to tb_app;

-- Platform operators are not tenant users (spec 004 FR-011). Platform tables, RLS exceptions.
create table identity.platform_operator (
  id uuid primary key,
  email text not null unique check (email = lower(email)),
  display_name text not null,
  status text not null check (status in ('active', 'disabled')),
  created_at timestamptz not null default now()
);
grant select, insert on identity.platform_operator to tb_app;

create table identity.platform_session (
  id uuid primary key,
  operator_id uuid not null references identity.platform_operator(id),
  token_hash text not null unique,
  created_at timestamptz not null default now(),
  expires_at timestamptz not null,
  revoked_at timestamptz null
);
grant select, insert, update on identity.platform_session to tb_app;
