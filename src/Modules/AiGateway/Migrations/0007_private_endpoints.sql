-- Spec 013 US6 and FR-004 (R2): private OpenAI-compatible model endpoints a tenant runs itself. secret_ref is a secret
-- manager reference of the same tenant, never the key. models lists the server's model names with their capabilities
-- and context size. An endpoint starts disabled; enabling needs a passed test less than 24 hours old (section 6), and any
-- change disables it again.
create table ai.private_endpoint (
  tenant_id uuid not null,
  id uuid not null,
  name text not null,
  base_url text not null,
  secret_ref text not null,
  models jsonb not null,
  region text not null,
  max_classification text not null check (max_classification in ('public', 'internal', 'confidential', 'restricted')),
  status text not null check (status in ('disabled', 'enabled')),
  version int not null,
  last_test_at timestamptz null,
  last_test_passed boolean null,
  last_test jsonb null,
  created_by uuid not null,
  created_at timestamptz not null,
  updated_at timestamptz not null,
  primary key (tenant_id, id)
);
select platform.enable_tenant_rls('ai.private_endpoint');
grant select, insert, update, delete on ai.private_endpoint to tb_app;
