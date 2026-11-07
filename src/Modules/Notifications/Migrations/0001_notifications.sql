-- Spec 015 (R1): in-app notifications, channel preferences, quiet hours, email deliveries and tenant settings.
-- Every table is tenant-owned with forced row-level security. Secrets are never stored: the relay password is a
-- secret manager reference.
create schema notifications;
grant usage on schema notifications to tb_app;

create table notifications.notification (
  tenant_id uuid not null,
  id uuid not null,
  user_id uuid not null,
  category text not null,
  mandatory boolean not null,
  urgent boolean not null default false,
  template_key text not null,
  parameters jsonb not null default '{}',
  link text not null check (link like '/%' and link not like '//%'),
  dedupe_key text null,
  group_key text null,
  group_count int not null default 1,
  created_at timestamptz not null,
  updated_at timestamptz not null,
  read_at timestamptz null,
  primary key (tenant_id, id)
);
create index notification_user_recent on notifications.notification (tenant_id, user_id, updated_at desc, id desc);
create index notification_user_unread on notifications.notification (tenant_id, user_id) where read_at is null;
create index notification_dedupe on notifications.notification (tenant_id, user_id, dedupe_key, created_at) where dedupe_key is not null;
create index notification_group on notifications.notification (tenant_id, user_id, group_key, updated_at) where group_key is not null and read_at is null;
select platform.enable_tenant_rls('notifications.notification');
grant select, insert, update, delete on notifications.notification to tb_app;

-- One row per notification and channel other than in-app. Pending rows wait until not_before (quiet hours, retries);
-- digest rows wait for the user's daily digest.
create table notifications.delivery (
  tenant_id uuid not null,
  id uuid not null,
  notification_id uuid not null,
  user_id uuid not null,
  channel text not null check (channel in ('email')),
  status text not null check (status in ('pending', 'digest', 'sent', 'failed', 'dropped')),
  address text null,
  attempts int not null default 0,
  not_before timestamptz not null,
  created_at timestamptz not null,
  sent_at timestamptz null,
  last_error text null,
  primary key (tenant_id, id),
  foreign key (tenant_id, notification_id) references notifications.notification (tenant_id, id) on delete cascade
);
create index delivery_due on notifications.delivery (tenant_id, not_before) where status = 'pending';
create index delivery_digest on notifications.delivery (tenant_id, user_id) where status = 'digest';
select platform.enable_tenant_rls('notifications.delivery');
grant select, insert, update, delete on notifications.delivery to tb_app;

create table notifications.preference (
  tenant_id uuid not null,
  user_id uuid not null,
  category text not null,
  email boolean not null,
  digest boolean not null default false,
  updated_at timestamptz not null,
  primary key (tenant_id, user_id, category)
);
select platform.enable_tenant_rls('notifications.preference');
grant select, insert, update, delete on notifications.preference to tb_app;

create table notifications.user_settings (
  tenant_id uuid not null,
  user_id uuid not null,
  quiet_start time null,
  quiet_end time null,
  time_zone text null,
  digest_time time not null default '08:00',
  last_digest_at timestamptz null,
  updated_at timestamptz not null,
  primary key (tenant_id, user_id),
  check ((quiet_start is null) = (quiet_end is null))
);
select platform.enable_tenant_rls('notifications.user_settings');
grant select, insert, update, delete on notifications.user_settings to tb_app;

-- Addresses whose mail hard bounced: email is paused until the address changes or an administrator clears it.
create table notifications.address_state (
  tenant_id uuid not null,
  address text not null,
  bounced_at timestamptz not null,
  reason text not null,
  primary key (tenant_id, address)
);
select platform.enable_tenant_rls('notifications.address_state');
grant select, insert, update, delete on notifications.address_state to tb_app;

create table notifications.tenant_settings (
  tenant_id uuid not null primary key,
  sender_name text null,
  relay_host text null,
  relay_port int null,
  relay_username text null,
  relay_secret_ref text null,
  relay_starttls boolean not null default true,
  default_email jsonb not null default '{}',
  extra_mandatory text[] not null default '{}',
  version int not null default 1,
  updated_at timestamptz not null,
  updated_by uuid null
);
select platform.enable_tenant_rls('notifications.tenant_settings');
grant select, insert, update, delete on notifications.tenant_settings to tb_app;

create table notifications.template_override (
  tenant_id uuid not null,
  template_key text not null,
  locale text not null,
  title text not null,
  body text not null,
  updated_at timestamptz not null,
  updated_by uuid null,
  primary key (tenant_id, template_key, locale)
);
select platform.enable_tenant_rls('notifications.template_override');
grant select, insert, update, delete on notifications.template_override to tb_app;

-- Development, test and staging only: mail the capture sender kept instead of sending (dev mailbox page).
create table notifications.captured_mail (
  tenant_id uuid not null,
  id uuid not null,
  to_address text not null,
  from_name text not null,
  subject text not null,
  text_body text not null,
  html_body text not null,
  captured_at timestamptz not null,
  primary key (tenant_id, id)
);
select platform.enable_tenant_rls('notifications.captured_mail');
grant select, insert, delete on notifications.captured_mail to tb_app;
