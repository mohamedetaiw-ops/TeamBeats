-- Specs 020 T001 and 027 T001 (R1): the agent registry, versions and the tenant agent policy. Platform templates and the
-- tool registry are declared in code (the plan's platform data, versioned with the product), so tenants hold only
-- their own agents. Every table is tenant-owned with forced RLS.

-- An agent of the tenant (spec 020 FR-002): its owner, owning org unit, template origin, lifecycle state, the version
-- in use and the risk class of that version (spec 027 US9).
create table agents.agent (
  tenant_id uuid not null,
  id uuid not null,
  template_id text null,
  template_version int null,
  owner_user_id uuid not null,
  org_unit_id uuid not null,
  status text not null check (status in ('draft', 'in_review', 'published', 'suspended', 'retired')),
  current_version int null,
  risk_class text not null check (risk_class in ('low', 'medium', 'high')),
  created_at timestamptz not null,
  created_by uuid not null,
  updated_at timestamptz not null,
  primary key (tenant_id, id)
);
create index agent_unit on agents.agent (tenant_id, org_unit_id);
select platform.enable_tenant_rls('agents.agent');
grant select, insert, update on agents.agent to tb_app;

-- Versions of an agent's definition (spec 020 FR-006). Published versions never change; a draft carries a revision
-- so a second designer's save over a newer draft is refused (edge case "two designers").
create table agents.agent_version (
  tenant_id uuid not null,
  agent_id uuid not null,
  number int not null check (number >= 1),
  state text not null check (state in ('draft', 'in_review', 'published', 'rejected')),
  definition jsonb not null,
  risk_class text not null check (risk_class in ('low', 'medium', 'high')),
  revision int not null default 1,
  last_editor uuid not null,
  submitted_by uuid null,
  approved_by uuid null,
  created_at timestamptz not null,
  updated_at timestamptz not null,
  published_at timestamptz null,
  primary key (tenant_id, agent_id, number),
  foreign key (tenant_id, agent_id) references agents.agent (tenant_id, id)
);
select platform.enable_tenant_rls('agents.agent_version');
grant select, insert, update on agents.agent_version to tb_app;

-- The tenant agent policy (spec 027 US8, tenant part), versioned. With no row the platform default applies.
create table agents.agent_policy (
  tenant_id uuid not null primary key,
  version int not null,
  policy jsonb not null,
  updated_at timestamptz not null,
  updated_by uuid not null
);
select platform.enable_tenant_rls('agents.agent_policy');
grant select, insert, update on agents.agent_policy to tb_app;
