-- Spec 033 R2 T019 (US4 scenario 3, FR-020): delegated experience administration. A tenant administrator, or an
-- organization administrator over their own part of the tree, gives a person the pages, brand or navigation of one
-- organization and those below it. Each area is a built-in role assigned at that subtree through the authorization
-- service, which keeps deciding every permission; this table remembers which assignment each area made.
create table experience.delegation (
  tenant_id uuid not null,
  org_unit_id uuid not null,
  user_id uuid not null,
  area text not null check (area in ('pages', 'brand', 'navigation')),
  assignment_id uuid not null,
  -- False when the person already held the role there through another assignment, which a removed delegation leaves.
  owned boolean not null,
  delegated_by uuid not null,
  delegated_at timestamptz not null,
  primary key (tenant_id, org_unit_id, user_id, area)
);
select platform.enable_tenant_rls('experience.delegation');
grant select, insert, delete on experience.delegation to tb_app;
