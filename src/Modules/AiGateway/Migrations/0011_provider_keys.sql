-- Spec 013 R3 (FR-020): a tenant's own provider key in dedicated and on-premises installs. secret_ref is a secret
-- manager reference of the same tenant, never the key. Calls use the key only while its last test passed.
create table ai.provider_key (
  tenant_id uuid not null,
  provider text not null check (provider in ('openrouter')),
  secret_ref text not null,
  last_test_at timestamptz null,
  last_test_passed boolean null,
  set_by uuid not null,
  set_at timestamptz not null,
  primary key (tenant_id, provider)
);
select platform.enable_tenant_rls('ai.provider_key');
grant select, insert, update, delete on ai.provider_key to tb_app;

-- Which key paid for a served call: the platform's, or the tenant's own (a provider key or a private endpoint).
alter table ai.usage_record add column key_owner text not null default 'platform' check (key_owner in ('platform', 'tenant'));
