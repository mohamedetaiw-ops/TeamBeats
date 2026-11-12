-- Spec 031 (R2): the workplace booking permissions and the facilities administrator and facilities analyst built-in
-- roles. Employees book; facilities administrators define sites, resources and rules and see reports in their scope;
-- analysts see reports. New tenants get the roles and grants from the seed; this adds them to tenants that already
-- exist, one tenant at a time with the tenant set (see 0007).
insert into authz.permission (key) values ('booking.book'), ('booking.manage'), ('booking.report')
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
    select r.tenant_id, gen_random_uuid(), k.key, true, true, false
    from authz.role r
    cross join (values ('facilities_administrator'), ('facilities_analyst')) as k(key)
    where r.key = 'tenant_administrator' and r.is_builtin
    on conflict (tenant_id, key) do nothing;

    insert into authz.role_permission (tenant_id, role_id, permission_key)
    select r.tenant_id, r.id, p.permission
    from authz.role r
    join (values
      ('tenant_administrator', 'booking.book'), ('tenant_administrator', 'booking.manage'), ('tenant_administrator', 'booking.report'),
      ('employee', 'booking.book'),
      ('facilities_administrator', 'org.view'), ('facilities_administrator', 'people.view'), ('facilities_administrator', 'booking.book'),
      ('facilities_administrator', 'booking.manage'), ('facilities_administrator', 'booking.report'),
      ('facilities_analyst', 'org.view'), ('facilities_analyst', 'booking.report')
    ) as p(role_key, permission) on p.role_key = r.key
    where r.is_builtin
    on conflict do nothing;
    update authz.grants_version set version = version + 1;
  end loop;
  perform set_config('app.tenant_id', '', true);
end $$;
