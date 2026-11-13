-- Spec 035 phase 3 (T009): the request pipeline of the public API. Per-app address ranges and call limits that a tenant
-- may only lower (section 6, FR-017, FR-027), the tenant's own limit for all apps, the call log kept 90 days without
-- bodies or query strings (section 6), and the monthly call counter checked against api.monthly_calls.
alter table publicapi.app add column ip_ranges text[] not null default '{}';
alter table publicapi.app add column calls_per_minute int null check (calls_per_minute between 1 and 3000);
alter table publicapi.setting add column tenant_calls_per_minute int null check (tenant_calls_per_minute >= 1);

-- One row per public API call that a token was accepted for: what was called, how it ended and how long it took.
-- error_class is null for a success, else rate_limited, quota, denied, not_found, client or server.
create table publicapi.call_log (
  tenant_id uuid not null,
  id uuid not null,
  at timestamptz not null,
  app_id uuid not null,
  user_id uuid null,
  operation text not null check (length(operation) <= 200),
  status int not null,
  error_class text null check (error_class in ('rate_limited', 'quota', 'denied', 'not_found', 'client', 'server')),
  latency_ms int not null,
  correlation_id text null check (length(correlation_id) <= 100),
  primary key (tenant_id, id),
  foreign key (tenant_id, app_id) references publicapi.app (tenant_id, id)
);
create index call_log_app_at on publicapi.call_log (tenant_id, app_id, at desc);
create index call_log_at on publicapi.call_log (tenant_id, at);
select platform.enable_tenant_rls('publicapi.call_log');
grant select, insert, delete on publicapi.call_log to tb_app;

-- Calls counted per calendar month (UTC) for the monthly quota, with the 80% and 100% alerts sent once each.
create table publicapi.usage_month (
  tenant_id uuid not null,
  month date not null,
  calls bigint not null default 0,
  alerted_80 boolean not null default false,
  alerted_100 boolean not null default false,
  primary key (tenant_id, month)
);
select platform.enable_tenant_rls('publicapi.usage_month');
grant select, insert, update, delete on publicapi.usage_month to tb_app;
