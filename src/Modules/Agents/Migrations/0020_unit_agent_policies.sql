-- Spec 027 US8 scenario 3 (R2): an org unit's agent policy, only the rules it narrows; null fields inherit.
create table agents.unit_agent_policy (
  tenant_id uuid not null,
  org_unit_id uuid not null,
  version int not null,
  policy jsonb not null,
  updated_at timestamptz not null,
  updated_by uuid not null,
  primary key (tenant_id, org_unit_id)
);
select platform.enable_tenant_rls('agents.unit_agent_policy');
grant select, insert, update, delete on agents.unit_agent_policy to tb_app;
