-- Spec 030 R3 (MOB-022): native push for the store apps. A device token belongs to one person and the session that
-- registered it, so signing out or revoking the session removes it, as for Web Push. The token is an address, not a
-- secret; the limit of 10 devices per person counts web and native devices together.
create table notifications.native_push_device (
  tenant_id uuid not null,
  id uuid not null,
  user_id uuid not null,
  session_id uuid null,
  platform text not null check (platform in ('android', 'ios')),
  token text not null check (length(token) between 32 and 4096),
  created_at timestamptz not null,
  primary key (tenant_id, id),
  unique (tenant_id, token)
);
create index native_push_device_user on notifications.native_push_device (tenant_id, user_id);
create index native_push_device_session on notifications.native_push_device (tenant_id, session_id) where session_id is not null;
select platform.enable_tenant_rls('notifications.native_push_device');
grant select, insert, update, delete on notifications.native_push_device to tb_app;
