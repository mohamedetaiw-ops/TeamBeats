-- Spec 026 T012 and T013: one alert per rule and window (dedupe key), and hourly download counts per user.
alter table security.alert add column dedupe_key text null;
alter table security.alert add column updated_at timestamptz not null default now();
create unique index alert_dedupe on security.alert (tenant_id, dedupe_key) where dedupe_key is not null;

create table security.download_count (
  tenant_id uuid not null,
  user_id uuid not null,
  hour_start timestamptz not null,
  files int not null default 0,
  bytes bigint not null default 0,
  libraries uuid[] not null default '{}',
  primary key (tenant_id, user_id, hour_start)
);
select platform.enable_tenant_rls('security.download_count');
grant select, insert, update, delete on security.download_count to tb_app;
