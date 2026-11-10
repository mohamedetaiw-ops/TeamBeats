-- Spec 017 US8 and spec 015 US8 scenario 3: a person's linked Slack account, per Slack connection. The link is made
-- by the person and found by their work email in the workspace; it is dropped when they unlink, when Slack says they
-- left the workspace, or when the connection is deleted. Tenant-owned, forced RLS.
create table integrations.slack_link (
  tenant_id uuid not null,
  user_id uuid not null,
  connection_id uuid not null,
  slack_user_id text not null check (slack_user_id ~ '^[UW][A-Z0-9]{8,19}$'),
  linked_at timestamptz not null,
  primary key (tenant_id, user_id),
  foreign key (tenant_id, connection_id) references integrations.connection (tenant_id, id) on delete cascade
);
create index slack_link_connection on integrations.slack_link (tenant_id, connection_id);
select platform.enable_tenant_rls('integrations.slack_link');
grant select, insert, update, delete on integrations.slack_link to tb_app;
