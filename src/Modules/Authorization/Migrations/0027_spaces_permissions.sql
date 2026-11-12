-- Spec 029 (R2): the spaces permissions, the spaces administrator role and the three space roles. Employees may create
-- spaces where the tenant allows; spaces administrators set the tenant rules. View, contribute and own come only from
-- the space roles, assigned on one space, so the tenant administrator does not hold them tenant-wide. New tenants get
-- the roles from the seed; this adds them to tenants that already exist, one tenant at a time (see 0007).
insert into authz.permission (key) values
  ('spaces.create'), ('spaces.manage'), ('spaces.view'), ('spaces.contribute'), ('spaces.own')
on conflict do nothing;

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
    insert into authz.role (tenant_id, id, key, is_builtin, is_delegable, is_delegated_admin)
    select r.tenant_id, gen_random_uuid(), k.key, true, k.delegable, false
    from authz.role r
    cross join (values ('spaces_administrator', true), ('space_owner', false), ('space_member', false), ('space_viewer', false)) as k(key, delegable)
    where r.key = 'tenant_administrator' and r.is_builtin
    on conflict (tenant_id, key) do nothing;

    insert into authz.role_permission (tenant_id, role_id, permission_key)
    select r.tenant_id, r.id, p.permission
    from authz.role r
    join (values
      ('tenant_administrator', 'spaces.create'), ('tenant_administrator', 'spaces.manage'),
      ('employee', 'spaces.create'),
      ('spaces_administrator', 'org.view'), ('spaces_administrator', 'people.view'),
      ('spaces_administrator', 'spaces.create'), ('spaces_administrator', 'spaces.manage'),
      ('space_owner', 'spaces.view'), ('space_owner', 'spaces.contribute'), ('space_owner', 'spaces.own'),
      ('space_member', 'spaces.view'), ('space_member', 'spaces.contribute'),
      ('space_viewer', 'spaces.view')
    ) as p(role_key, permission) on p.role_key = r.key
    where r.is_builtin
    on conflict do nothing;
    update authz.grants_version set version = version + 1;
  end loop;
  perform set_config('app.tenant_id', '', true);
end $$;
