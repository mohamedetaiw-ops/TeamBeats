-- Spec 027 R3 (FR-006): the tools of external MCP servers. TeamBeats keeps each tool the server lists with its
-- description and input schema; a tool is usable by agents only once the integration administrator approved it with
-- a name, a side effect and titles, and only while its description and schema are the ones approved. Tenant-owned,
-- forced RLS.
create table integrations.mcp_tool (
  tenant_id uuid not null,
  id uuid not null,
  connection_id uuid not null,
  remote_name text not null check (length(remote_name) between 1 and 128),
  description text not null check (length(description) <= 2000),
  input_schema jsonb not null,
  schema_hash text not null,
  read_only_hint boolean not null default false,
  status text not null check (status in ('pending', 'approved', 'rejected', 'unsupported')),
  listed boolean not null default true,
  name text null check (name ~ '^[a-z][a-z0-9_]{2,40}$'),
  side_effect text null check (side_effect in ('read', 'write_external')),
  title jsonb null,
  approved_hash text null,
  decided_by uuid null,
  decided_at timestamptz null,
  seen_at timestamptz not null,
  created_at timestamptz not null default now(),
  primary key (tenant_id, id),
  unique (tenant_id, connection_id, remote_name),
  foreign key (tenant_id, connection_id) references integrations.connection (tenant_id, id) on delete cascade
);
create unique index mcp_tool_name on integrations.mcp_tool (tenant_id, name) where name is not null;
select platform.enable_tenant_rls('integrations.mcp_tool');
grant select, insert, update, delete on integrations.mcp_tool to tb_app;
