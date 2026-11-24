-- Spec 005 R2 US4 (FR-004): add-ons and contract overrides on top of the package version. Effective value: package,
-- then an active add-on, then an active override. A row outside its validity window is ignored, so an override
-- reverts on its end date without a job. Tenant-owned and confidential (commercial); only the platform sets them.
create table entitlements.addon (
  tenant_id uuid not null,
  capability_key text not null references entitlements.capability(key),
  enabled boolean not null,
  limit_value bigint null check (limit_value is null or limit_value >= 0),
  valid_from timestamptz not null default now(),
  valid_to timestamptz null,
  reason text null check (reason is null or length(reason) <= 500),
  set_by uuid null,
  updated_at timestamptz not null default now(),
  primary key (tenant_id, capability_key),
  check (valid_to is null or valid_to > valid_from)
);
select platform.enable_tenant_rls('entitlements.addon');
grant select, insert, update, delete on entitlements.addon to tb_app;

-- A contract override always has an end date.
create table entitlements.override (
  tenant_id uuid not null,
  capability_key text not null references entitlements.capability(key),
  enabled boolean not null,
  limit_value bigint null check (limit_value is null or limit_value >= 0),
  valid_from timestamptz not null default now(),
  valid_to timestamptz not null,
  reason text null check (reason is null or length(reason) <= 500),
  set_by uuid null,
  updated_at timestamptz not null default now(),
  primary key (tenant_id, capability_key),
  check (valid_to > valid_from)
);
select platform.enable_tenant_rls('entitlements.override');
grant select, insert, update, delete on entitlements.override to tb_app;
