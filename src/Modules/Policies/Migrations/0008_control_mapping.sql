-- Spec 025 US8 (R2, FR-015): policy to control mapping. A policy links to controls and regulation clauses of the spec
-- 026 control library by framework key and control code; the library itself stays in the Compliance module.
create table policies.policy_control (
  tenant_id uuid not null,
  policy_id uuid not null,
  framework_key text not null check (length(framework_key) between 1 and 60),
  control_code text not null check (length(control_code) between 1 and 20),
  added_by uuid not null,
  added_at timestamptz not null,
  primary key (tenant_id, policy_id, framework_key, control_code),
  foreign key (tenant_id, policy_id) references policies.policy (tenant_id, id)
);
create index policy_control_by_control on policies.policy_control (tenant_id, framework_key, control_code);
select platform.enable_tenant_rls('policies.policy_control');
grant select, insert, delete on policies.policy_control to tb_app;
