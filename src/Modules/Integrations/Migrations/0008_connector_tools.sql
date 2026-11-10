-- Spec 017 (R2) T009, spec 027 FR-006: connector actions the integration administrator registered as agent tools. The
-- tool is named connector.<name>; its parameters are the action's declared inputs, read from the action each time, so a
-- changed action never leaves a stale schema. The description tells the model what the tool does; the titles are what
-- people see. Tenant-owned, forced RLS.
create table integrations.connector_tool (
  tenant_id uuid not null,
  id uuid not null,
  connection_id uuid not null,
  action text not null check (action ~ '^[a-z][a-z0-9_]{0,62}$'),
  name text not null check (name ~ '^[a-z][a-z0-9_]{2,40}$'),
  description text not null check (length(description) between 10 and 500),
  title jsonb not null,
  created_by uuid null,
  created_at timestamptz not null default now(),
  primary key (tenant_id, id),
  foreign key (tenant_id, connection_id) references integrations.connection (tenant_id, id) on delete cascade
);
create unique index connector_tool_name on integrations.connector_tool (tenant_id, name);
create unique index connector_tool_action on integrations.connector_tool (tenant_id, connection_id, action);
select platform.enable_tenant_rls('integrations.connector_tool');
grant select, insert, update, delete on integrations.connector_tool to tb_app;
