-- Spec 005 R0: capability catalogue, versioned packages, tenant subscriptions.
-- Seed values mirror the spec 005 section 6 table. Quota numbers are open commercial items (C1, C2),
-- so limits are seeded as NULL (no limit configured) and are data, not code.
create schema entitlements;
grant usage on schema entitlements to tb_app;

-- Platform tables (RLS exceptions, see rls-exceptions.md).
create table entitlements.capability (
  key text primary key,
  type text not null check (type in ('flag', 'limit')),
  unit text null,
  description_key text not null
);
grant select on entitlements.capability to tb_app;

create table entitlements.package (
  key text primary key,
  name_key text not null
);
grant select, insert on entitlements.package to tb_app;

create table entitlements.package_version (
  id uuid primary key,
  package_key text not null references entitlements.package(key),
  version int not null,
  status text not null check (status in ('draft', 'published', 'retired')),
  created_at timestamptz not null default now(),
  published_at timestamptz null,
  unique (package_key, version)
);
grant select, insert, update on entitlements.package_version to tb_app;

create table entitlements.package_capability (
  package_version_id uuid not null references entitlements.package_version(id),
  capability_key text not null references entitlements.capability(key),
  enabled boolean not null,
  limit_value bigint null,
  primary key (package_version_id, capability_key)
);
grant select, insert on entitlements.package_capability to tb_app;

-- AI is in every package (constitution XIII, AI-001): a published version must enable ai.assistant.
create function entitlements.check_ai_in_package() returns trigger
language plpgsql
as $$
begin
  if new.status = 'published' and not exists (
      select 1 from entitlements.package_capability
      where package_version_id = new.id and capability_key = 'ai.assistant' and enabled) then
    raise exception 'package version % must enable ai.assistant', new.id using errcode = 'check_violation';
  end if;
  return new;
end;
$$;
create trigger package_version_ai_check before insert or update of status on entitlements.package_version
  for each row execute function entitlements.check_ai_in_package();

-- Tenant-owned.
create table entitlements.subscription (
  tenant_id uuid not null,
  id uuid not null,
  package_version_id uuid not null references entitlements.package_version(id),
  starts_at timestamptz not null,
  ends_at timestamptz null,
  status text not null check (status in ('active', 'ended')),
  expiry_grace_days int not null default 30,
  primary key (tenant_id, id)
);
create unique index subscription_one_active on entitlements.subscription (tenant_id) where status = 'active';
select platform.enable_tenant_rls('entitlements.subscription');
grant select, insert, update on entitlements.subscription to tb_app;

insert into entitlements.capability (key, type, unit, description_key) values
  ('ai.assistant', 'flag', null, 'capability.ai.assistant'),
  ('ai.assistant.monthly_requests', 'limit', 'requests', 'capability.ai.assistant.monthly_requests'),
  ('ai.agents.basic', 'flag', null, 'capability.ai.agents.basic'),
  ('ai.agents.write_tools', 'flag', null, 'capability.ai.agents.write_tools'),
  ('ai.model_choice', 'flag', null, 'capability.ai.model_choice'),
  ('org.hierarchy', 'flag', null, 'capability.org.hierarchy'),
  ('org.delegated_admin', 'flag', null, 'capability.org.delegated_admin'),
  ('org.max_units', 'limit', 'units', 'capability.org.max_units'),
  ('authz.custom_roles', 'flag', null, 'capability.authz.custom_roles'),
  ('identity.oidc', 'flag', null, 'capability.identity.oidc'),
  ('identity.saml', 'flag', null, 'capability.identity.saml'),
  ('identity.scim', 'flag', null, 'capability.identity.scim'),
  ('workflow.advanced', 'flag', null, 'capability.workflow.advanced'),
  ('integration.microsoft365', 'flag', null, 'capability.integration.microsoft365'),
  ('integration.oracle_fusion', 'flag', null, 'capability.integration.oracle_fusion'),
  ('integration.sap', 'flag', null, 'capability.integration.sap'),
  ('analytics.advanced', 'flag', null, 'capability.analytics.advanced'),
  ('analytics.export', 'flag', null, 'capability.analytics.export'),
  ('deployment.on_prem', 'flag', null, 'capability.deployment.on_prem');

insert into entitlements.package (key, name_key) values
  ('workplace', 'package.workplace'),
  ('workplace_plus', 'package.workplace_plus'),
  ('enterprise_gov', 'package.enterprise_gov');

-- Version rows are inserted as draft, capabilities added, then published (the trigger checks ai.assistant).
insert into entitlements.package_version (id, package_key, version, status) values
  ('00000000-0000-7000-8000-000000000101', 'workplace', 1, 'draft'),
  ('00000000-0000-7000-8000-000000000102', 'workplace_plus', 1, 'draft'),
  ('00000000-0000-7000-8000-000000000103', 'enterprise_gov', 1, 'draft');
insert into entitlements.package_capability (package_version_id, capability_key, enabled, limit_value) values
  ('00000000-0000-7000-8000-000000000101', 'ai.assistant', true, null),
  ('00000000-0000-7000-8000-000000000102', 'ai.assistant', true, null),
  ('00000000-0000-7000-8000-000000000103', 'ai.assistant', true, null),
  ('00000000-0000-7000-8000-000000000101', 'ai.assistant.monthly_requests', true, null),
  ('00000000-0000-7000-8000-000000000102', 'ai.assistant.monthly_requests', true, null),
  ('00000000-0000-7000-8000-000000000103', 'ai.assistant.monthly_requests', true, null),
  ('00000000-0000-7000-8000-000000000101', 'ai.agents.basic', true, null),
  ('00000000-0000-7000-8000-000000000102', 'ai.agents.basic', true, null),
  ('00000000-0000-7000-8000-000000000103', 'ai.agents.basic', true, null),
  ('00000000-0000-7000-8000-000000000101', 'ai.agents.write_tools', false, null),
  ('00000000-0000-7000-8000-000000000102', 'ai.agents.write_tools', true, null),
  ('00000000-0000-7000-8000-000000000103', 'ai.agents.write_tools', true, null),
  ('00000000-0000-7000-8000-000000000101', 'ai.model_choice', false, null),
  ('00000000-0000-7000-8000-000000000102', 'ai.model_choice', true, null),
  ('00000000-0000-7000-8000-000000000103', 'ai.model_choice', true, null),
  ('00000000-0000-7000-8000-000000000101', 'org.hierarchy', true, null),
  ('00000000-0000-7000-8000-000000000102', 'org.hierarchy', true, null),
  ('00000000-0000-7000-8000-000000000103', 'org.hierarchy', true, null),
  ('00000000-0000-7000-8000-000000000101', 'org.delegated_admin', false, null),
  ('00000000-0000-7000-8000-000000000102', 'org.delegated_admin', true, null),
  ('00000000-0000-7000-8000-000000000103', 'org.delegated_admin', true, null),
  ('00000000-0000-7000-8000-000000000101', 'org.max_units', true, null),
  ('00000000-0000-7000-8000-000000000102', 'org.max_units', true, null),
  ('00000000-0000-7000-8000-000000000103', 'org.max_units', true, null),
  ('00000000-0000-7000-8000-000000000101', 'authz.custom_roles', false, null),
  ('00000000-0000-7000-8000-000000000102', 'authz.custom_roles', true, null),
  ('00000000-0000-7000-8000-000000000103', 'authz.custom_roles', true, null),
  ('00000000-0000-7000-8000-000000000101', 'identity.oidc', true, null),
  ('00000000-0000-7000-8000-000000000102', 'identity.oidc', true, null),
  ('00000000-0000-7000-8000-000000000103', 'identity.oidc', true, null),
  ('00000000-0000-7000-8000-000000000101', 'identity.saml', true, null),
  ('00000000-0000-7000-8000-000000000102', 'identity.saml', true, null),
  ('00000000-0000-7000-8000-000000000103', 'identity.saml', true, null),
  ('00000000-0000-7000-8000-000000000101', 'identity.scim', false, null),
  ('00000000-0000-7000-8000-000000000102', 'identity.scim', true, null),
  ('00000000-0000-7000-8000-000000000103', 'identity.scim', true, null),
  ('00000000-0000-7000-8000-000000000101', 'workflow.advanced', false, null),
  ('00000000-0000-7000-8000-000000000102', 'workflow.advanced', true, null),
  ('00000000-0000-7000-8000-000000000103', 'workflow.advanced', true, null),
  ('00000000-0000-7000-8000-000000000101', 'integration.microsoft365', false, null),
  ('00000000-0000-7000-8000-000000000102', 'integration.microsoft365', true, null),
  ('00000000-0000-7000-8000-000000000103', 'integration.microsoft365', true, null),
  ('00000000-0000-7000-8000-000000000101', 'integration.oracle_fusion', false, null),
  ('00000000-0000-7000-8000-000000000102', 'integration.oracle_fusion', false, null),
  ('00000000-0000-7000-8000-000000000103', 'integration.oracle_fusion', true, null),
  ('00000000-0000-7000-8000-000000000101', 'integration.sap', false, null),
  ('00000000-0000-7000-8000-000000000102', 'integration.sap', false, null),
  ('00000000-0000-7000-8000-000000000103', 'integration.sap', true, null),
  ('00000000-0000-7000-8000-000000000101', 'analytics.advanced', false, null),
  ('00000000-0000-7000-8000-000000000102', 'analytics.advanced', true, null),
  ('00000000-0000-7000-8000-000000000103', 'analytics.advanced', true, null),
  ('00000000-0000-7000-8000-000000000101', 'analytics.export', false, null),
  ('00000000-0000-7000-8000-000000000102', 'analytics.export', false, null),
  ('00000000-0000-7000-8000-000000000103', 'analytics.export', true, null),
  ('00000000-0000-7000-8000-000000000101', 'deployment.on_prem', false, null),
  ('00000000-0000-7000-8000-000000000102', 'deployment.on_prem', false, null),
  ('00000000-0000-7000-8000-000000000103', 'deployment.on_prem', true, null);
update entitlements.package_version set status = 'published', published_at = now() where version = 1;
