-- Spec 006 User Story 5 (R1): the tenant's connections to external systems and their features.
-- Secrets are never stored here; the Microsoft 365 connection uses the TeamBeats application with admin consent.
create schema integrations;
grant usage on schema integrations to tb_app;

create table integrations.connection (
  tenant_id uuid not null,
  id uuid not null,
  kind text not null check (kind in ('microsoft365')),
  external_tenant_id text not null,
  display_name text not null,
  status text not null check (status in ('healthy', 'degraded', 'needs_consent', 'credential_expiring', 'disabled')),
  enabled boolean not null default true,
  owner_user_id uuid null,
  last_success_at timestamptz null,
  last_checked_at timestamptz null,
  created_at timestamptz not null default now(),
  updated_at timestamptz not null default now(),
  primary key (tenant_id, id),
  unique (tenant_id, kind, external_tenant_id)
);
select platform.enable_tenant_rls('integrations.connection');
grant select, insert, update, delete on integrations.connection to tb_app;

create table integrations.connection_feature (
  tenant_id uuid not null,
  connection_id uuid not null,
  feature text not null,
  enabled boolean not null default false,
  consent_status text not null default 'pending' check (consent_status in ('pending', 'granted')),
  granted_permissions text[] not null default '{}',
  consent_granted_at timestamptz null,
  last_test jsonb null,
  updated_at timestamptz not null default now(),
  primary key (tenant_id, connection_id, feature),
  foreign key (tenant_id, connection_id) references integrations.connection (tenant_id, id) on delete cascade
);
select platform.enable_tenant_rls('integrations.connection_feature');
grant select, insert, update, delete on integrations.connection_feature to tb_app;

-- Pending admin consent requests: state and browser binding stored as hashes, single use.
create table integrations.consent_request (
  tenant_id uuid not null,
  id uuid not null,
  connection_id uuid not null,
  feature text not null,
  state_hash text not null,
  browser_binding_hash text not null,
  created_at timestamptz not null default now(),
  expires_at timestamptz not null,
  consumed_at timestamptz null,
  primary key (tenant_id, id),
  unique (tenant_id, state_hash),
  foreign key (tenant_id, connection_id) references integrations.connection (tenant_id, id) on delete cascade
);
select platform.enable_tenant_rls('integrations.consent_request');
grant select, insert, update, delete on integrations.consent_request to tb_app;
