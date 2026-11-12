-- Spec 030 US2 (R2): Web Push as a spec 015 channel. A subscription belongs to one person and the session that made it,
-- so signing out or revoking the session removes it. The browser's keys are public halves only; nothing secret is stored.
create table notifications.push_subscription (
  tenant_id uuid not null,
  id uuid not null,
  user_id uuid not null,
  session_id uuid null,
  endpoint text not null check (endpoint like 'https://%'),
  p256dh text not null,
  auth text not null,
  platform text not null check (platform in ('android', 'ios', 'windows', 'macos', 'linux', 'other')),
  created_at timestamptz not null,
  primary key (tenant_id, id),
  unique (tenant_id, endpoint)
);
create index push_subscription_user on notifications.push_subscription (tenant_id, user_id);
create index push_subscription_session on notifications.push_subscription (tenant_id, session_id) where session_id is not null;
select platform.enable_tenant_rls('notifications.push_subscription');
grant select, insert, update, delete on notifications.push_subscription to tb_app;

-- Push is on for every category until the person turns it off, and only reaches devices where they allowed it.
alter table notifications.preference add column push boolean not null default true;

alter table notifications.delivery drop constraint delivery_channel_check;
alter table notifications.delivery add constraint delivery_channel_check check (channel in ('email', 'slack', 'push'));
