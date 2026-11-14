-- Spec 013 US7 and FR-009 (R2): an org unit policy only restricts the policy above it. document holds the fields the
-- unit narrows (allowlist, Confidential models, highest classification, output tokens, temperature range); a field that
-- is not set follows the parent. version is the optimistic concurrency token sent as the ETag.
create table ai.org_policy (
  tenant_id uuid not null,
  unit_id uuid not null,
  version int not null,
  document jsonb not null,
  updated_by uuid not null,
  updated_at timestamptz not null,
  primary key (tenant_id, unit_id)
);
select platform.enable_tenant_rls('ai.org_policy');
grant select, insert, update, delete on ai.org_policy to tb_app;
