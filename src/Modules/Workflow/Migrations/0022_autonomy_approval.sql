-- Spec 027 T023 (R3, FR-024): a published process version with agent tasks runs them only after a process owner and a
-- holder of ai.policy_manage, two different people, approved the version for autonomous agent steps. One row per
-- version; a new version needs both again. Tenant-owned, forced RLS.
create table workflow.autonomy_approval (
  tenant_id uuid not null,
  definition_key text not null,
  version int not null,
  owner_by uuid null,
  owner_at timestamptz null,
  policy_by uuid null,
  policy_at timestamptz null,
  primary key (tenant_id, definition_key, version),
  check (owner_by is null or policy_by is null or owner_by <> policy_by)
);
select platform.enable_tenant_rls('workflow.autonomy_approval');
grant select, insert, update on workflow.autonomy_approval to tb_app;
