-- Spec 035 FR-023 (slice 4d): the platform's verified list of MCP clients. Each entry is a client ID metadata document
-- URL with the name, publisher, redirect URIs and scopes the platform operator recorded at the security review. A
-- tenant's AI policy administrator allows a listed client; the first employee who connects it then creates the tenant's
-- app registration, marked verified, whose name, publisher, scopes and redirect URIs always come from this list. A
-- blocked or removed entry disables every tenant's registration of it at once. Platform data, no tenant column.
create table publicapi.verified_mcp_client (
  client_id text primary key check (length(client_id) between 12 and 200 and client_id like 'https://%'),
  name text not null check (length(name) between 1 and 80),
  publisher text not null check (length(publisher) between 1 and 80),
  redirect_uris text[] not null,
  scopes text[] not null,
  status text not null default 'active' check (status in ('active', 'blocked')),
  reviewed_by uuid not null,
  reviewed_at timestamptz not null,
  created_at timestamptz not null default now()
);
grant select, insert, update on publicapi.verified_mcp_client to tb_app;

alter table publicapi.app add column verified boolean not null default false;
