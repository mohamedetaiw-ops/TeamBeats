-- Spec 032 R2 T031 (FR-033): independent component deployment. When a tenant upgrades a component to its next major, the
-- deployment is kept here, and live releases that still place the old major show the new one through the migration hook
-- without being republished. Removing the row rolls live pages back to the old major; releases themselves never change.
create table experience.component_deployment (
  tenant_id uuid not null,
  component_id text not null check (length(component_id) between 1 and 120),
  from_major int not null check (from_major >= 1),
  to_major int not null check (to_major = from_major + 1),
  deployed_by uuid not null,
  deployed_at timestamptz not null,
  primary key (tenant_id, component_id, from_major)
);
select platform.enable_tenant_rls('experience.component_deployment');
grant select, insert, update, delete on experience.component_deployment to tb_app;
