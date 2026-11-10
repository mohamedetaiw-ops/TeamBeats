-- Spec 017 (R2) T007 and T012: the lifecycle of connections of any pack, and the actions a tenant defines for a custom
-- REST connection. Credential values never reach this schema: settings hold secret manager references only.
alter table integrations.connection
  add column credential_expires_at date null,
  add column last_test jsonb null;

create table integrations.connector_action (
  tenant_id uuid not null,
  connection_id uuid not null,
  name text not null check (name ~ '^[a-z][a-z0-9_]{0,62}$'),
  side_effect text not null check (side_effect in ('read', 'write', 'external_effect')),
  definition jsonb not null,
  created_by uuid null,
  created_at timestamptz not null default now(),
  updated_by uuid null,
  updated_at timestamptz not null default now(),
  primary key (tenant_id, connection_id, name),
  foreign key (tenant_id, connection_id) references integrations.connection (tenant_id, id) on delete cascade
);
select platform.enable_tenant_rls('integrations.connector_action');
grant select, insert, update, delete on integrations.connector_action to tb_app;
