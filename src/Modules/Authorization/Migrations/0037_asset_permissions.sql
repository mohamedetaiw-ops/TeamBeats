-- Spec 011 R2 US5: brand managers (the role themes already use, 0004) also curate the asset portal. New tenants get the
-- grants from the seed; this adds them to tenants that already exist, one tenant at a time (see 0007). assets.view is not in the Employee role: open
-- collections are read by every member through tenant visibility, restricted ones through their ACL.
insert into authz.permission (key) values ('assets.manage'), ('assets.view')
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
    select r.tenant_id, gen_random_uuid(), 'brand_manager', true, true, false
    from authz.role r
    where r.key = 'tenant_administrator' and r.is_builtin
    on conflict (tenant_id, key) do nothing;

    insert into authz.role_permission (tenant_id, role_id, permission_key)
    select r.tenant_id, r.id, p.permission
    from authz.role r
    join (values
      ('tenant_administrator', 'assets.manage'), ('tenant_administrator', 'assets.view'),
      ('brand_manager', 'org.view'), ('brand_manager', 'people.view'), ('brand_manager', 'assets.manage'), ('brand_manager', 'assets.view')
    ) as p(role_key, permission) on p.role_key = r.key
    where r.is_builtin
    on conflict do nothing;
    update authz.grants_version set version = version + 1;
  end loop;
  perform set_config('app.tenant_id', '', true);
end $$;
