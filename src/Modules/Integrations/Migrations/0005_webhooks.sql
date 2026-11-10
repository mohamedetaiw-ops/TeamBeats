-- Spec 017 (R2) T013: inbound webhooks. Delivery IDs seen per connection, kept a day so a repeated delivery is refused
-- well past the longest timestamp window (5 minutes), and the counts of accepted and refused deliveries. Outbound
-- deliveries are runs of the webhook pack, so their log is the run history. Tenant-owned, forced RLS.
create table integrations.webhook_receipt (
  tenant_id uuid not null,
  connection_id uuid not null,
  delivery_id text not null check (length(delivery_id) between 1 and 200),
  received_at timestamptz not null,
  primary key (tenant_id, connection_id, delivery_id),
  foreign key (tenant_id, connection_id) references integrations.connection (tenant_id, id) on delete cascade
);
create index webhook_receipt_age on integrations.webhook_receipt (tenant_id, received_at);
select platform.enable_tenant_rls('integrations.webhook_receipt');
grant select, insert, delete on integrations.webhook_receipt to tb_app;

create table integrations.webhook_inbound_count (
  tenant_id uuid not null,
  connection_id uuid not null,
  accepted bigint not null default 0,
  refused_signature bigint not null default 0,
  refused_window bigint not null default 0,
  refused_duplicate bigint not null default 0,
  last_received_at timestamptz null,
  primary key (tenant_id, connection_id),
  foreign key (tenant_id, connection_id) references integrations.connection (tenant_id, id) on delete cascade
);
select platform.enable_tenant_rls('integrations.webhook_inbound_count');
grant select, insert, update, delete on integrations.webhook_inbound_count to tb_app;
