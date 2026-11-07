-- Spec 010 (R1): people.view for employees, people.hr_view for HR officers, people.manage for tenant and organization
-- administrators. New tenants get the grants from the seed; this adds them to tenants that already exist, one tenant
-- at a time with the tenant set, because role tables force row-level security (see 0007).
insert into authz.permission (key) values ('people.view'), ('people.hr_view'), ('people.manage') on conflict do nothing;

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
      ('tenant_administrator', 'people.view'), ('tenant_administrator', 'people.hr_view'), ('tenant_administrator', 'people.manage'),
      ('organization_administrator', 'people.view'), ('organization_administrator', 'people.manage'),
      ('employee', 'people.view'),
      ('hr_officer', 'people.view'), ('hr_officer', 'people.hr_view')
    ) as p(role_key, permission) on p.role_key = r.key
    where r.is_builtin
    on conflict do nothing;
    update authz.grants_version set version = version + 1;
  end loop;
  perform set_config('app.tenant_id', '', true);
end $$;
