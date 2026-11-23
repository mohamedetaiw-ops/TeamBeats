-- Spec 033 R2 T019 (US4 scenarios 4 and 5, FR-021, FR-022): sharing policies. A page owned by an organization reaches its
-- own branch (the organization, those below it and those above it). People of another branch see it only when its
-- owner shares it with their organization (and the organizations below), which needs the owner's publish right. Sharing
-- changes what people see, never what they may do with data.
create table experience.experience_share (
  tenant_id uuid not null,
  kind text not null check (kind in ('page', 'menu')),
  resource_id uuid not null,
  org_unit_id uuid not null,
  shared_by uuid not null,
  shared_at timestamptz not null,
  primary key (tenant_id, kind, resource_id, org_unit_id)
);
select platform.enable_tenant_rls('experience.experience_share');
grant select, insert, delete on experience.experience_share to tb_app;
