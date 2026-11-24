-- Spec 015 US5 scenario 2 (R2, FR-018): quick actions from Teams. A notification may name the owning module's handler,
-- the thing it acts on and the actions offered. Each Teams card carries one random token per action, kept only as a
-- SHA-256 hash, bound to the person and the notification, good for 7 days and for one use.
alter table notifications.notification add column actions jsonb null;

create table notifications.quick_action (
  tenant_id uuid not null,
  token_hash text not null,
  notification_id uuid not null,
  user_id uuid not null,
  action text not null check (length(action) between 1 and 40),
  expires_at timestamptz not null,
  used_at timestamptz null,
  primary key (tenant_id, token_hash),
  foreign key (tenant_id, notification_id) references notifications.notification (tenant_id, id) on delete cascade
);
create index quick_action_notification on notifications.quick_action (tenant_id, notification_id);
select platform.enable_tenant_rls('notifications.quick_action');
grant select, insert, update, delete on notifications.quick_action to tb_app;
