-- Spec 005 R2 US3 (FR-006): usage per tenant, capability and calendar month (UTC), and how a quota is enforced.
-- A module either consumes through the entitlement service (the counter is the meter) or keeps its own meter and asks
-- with its count (the AI Gateway's call records); either way the counter shows this period's usage.
create table entitlements.usage_counter (
  tenant_id uuid not null,
  capability_key text not null references entitlements.capability(key),
  period text not null check (period ~ '^[0-9]{4}-[0-9]{2}$'),
  used bigint not null default 0 check (used >= 0),
  exhausted_at timestamptz null,
  updated_at timestamptz not null default now(),
  primary key (tenant_id, capability_key, period)
);
select platform.enable_tenant_rls('entitlements.usage_counter');
grant select, insert, update on entitlements.usage_counter to tb_app;

-- deny (default): refuse at the limit. grace: allow grace_pct over the limit with warnings. warn: never refuse.
create table entitlements.enforcement (
  tenant_id uuid not null,
  capability_key text not null references entitlements.capability(key),
  mode text not null check (mode in ('deny', 'grace', 'warn')),
  grace_pct int not null default 0 check (grace_pct between 0 and 100),
  updated_at timestamptz not null default now(),
  primary key (tenant_id, capability_key),
  check (mode = 'grace' or grace_pct = 0)
);
select platform.enable_tenant_rls('entitlements.enforcement');
grant select, insert, update, delete on entitlements.enforcement to tb_app;
