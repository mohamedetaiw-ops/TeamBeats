-- Spec 029 FR-006: a space's library has the space's members as its audience. The space roles, assigned on the space,
-- also apply to the space's library through its container (see ResourceDescriptor.Container): owners manage the
-- library, members add files, viewers read. Existing tenants get the permissions here, one tenant at a time (see 0007).
do $$
declare
  tenants uuid[];
  tenant uuid;
begin
  alter table authz.role no force row level security;
  select coalesce(array_agg(distinct tenant_id), '{}') into tenants from authz.role;
  alter table authz.role force row level security;

  foreach tenant in array tenants loop
    perform set_config('app.tenant_id', tenant::text, true);
    insert into authz.role_permission (tenant_id, role_id, permission_key)
    select r.tenant_id, r.id, p.permission
    from authz.role r
    join (values
      ('space_owner', 'doc.view'), ('space_owner', 'doc.contribute'), ('space_owner', 'doc.manage'),
      ('space_member', 'doc.view'), ('space_member', 'doc.contribute'),
      ('space_viewer', 'doc.view')
    ) as p(role_key, permission) on p.role_key = r.key
    where r.is_builtin
    on conflict do nothing;
    update authz.grants_version set version = version + 1;
  end loop;
  perform set_config('app.tenant_id', '', true);
end $$;
