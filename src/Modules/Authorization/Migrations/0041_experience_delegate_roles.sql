-- Spec 033 R2 T019 (FR-020): the Brand Publisher and Navigation Editor roles, so a tenant administrator can delegate one
-- organization's brand or navigation. New tenants get them from the seed; this adds them to tenants that already exist,
-- one tenant at a time (see 0007).
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
    select r.tenant_id, gen_random_uuid(), k.key, true, true, false
    from authz.role r
    cross join (values ('brand_publisher'), ('navigation_editor')) as k(key)
    where r.key = 'tenant_administrator' and r.is_builtin
    on conflict (tenant_id, key) do nothing;

    insert into authz.role_permission (tenant_id, role_id, permission_key)
    select r.tenant_id, r.id, p.permission
    from authz.role r
    join (values
      ('brand_publisher', 'org.view'), ('brand_publisher', 'theme.edit'), ('brand_publisher', 'theme.publish'),
      ('navigation_editor', 'org.view'), ('navigation_editor', 'navigation.edit')) as p(role_key, permission) on p.role_key = r.key
    where r.is_builtin
    on conflict do nothing;
    update authz.grants_version set version = version + 1;
  end loop;
  perform set_config('app.tenant_id', '', true);
end $$;
