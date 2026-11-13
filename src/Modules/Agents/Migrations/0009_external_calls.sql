-- Spec 027 FR-025 and spec 035 US3 scenario 7 (R2): the execution record of each tool call an external AI client made
-- through the MCP server: the MCP client app, the employee it acted for, the tool, the parameter hash, the parameters
-- (masked when the result was Confidential), the gateway's decision and reason, the classification of what went back,
-- and how long it took. Kept as long as agent run records. Tenant-owned with forced RLS.
create table agents.external_call (
  tenant_id uuid not null,
  id uuid not null,
  app_id uuid not null,
  client_id text not null,
  user_id uuid not null,
  tool text not null,
  parameter_hash text null,
  parameters jsonb null,
  decision text not null check (decision in ('executed', 'refused')),
  reason text null,
  classification text null,
  duration_ms int not null default 0,
  at timestamptz not null,
  primary key (tenant_id, id)
);
create index external_call_user on agents.external_call (tenant_id, user_id, app_id, tool, at desc);
create index external_call_at on agents.external_call (tenant_id, at desc);
select platform.enable_tenant_rls('agents.external_call');
grant select, insert, delete on agents.external_call to tb_app;
