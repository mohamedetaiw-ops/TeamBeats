-- Spec 035 US1 scenario 4 and section 6 requests:submit (R2): reading the service requests of people in the units held,
-- and the two request roles an application app can hold. The request reader reads them; the request filer also files a
-- request for one of those people. New tenants get them from the seed; this adds them to tenants that already exist, one
-- tenant at a time (see 0007).
insert into authz.permission (key) values ('service.request_read')
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
    insert into authz.role (tenant_id, id, key, is_builtin, is_delegable, is_delegated_admin, assignable_to_apps)
    select r.tenant_id, gen_random_uuid(), k.key, true, true, false, true
    from authz.role r
    cross join (values ('app_request_reader'), ('app_request_filer')) as k(key)
    where r.key = 'tenant_administrator' and r.is_builtin
    on conflict (tenant_id, key) do nothing;

    insert into authz.role_permission (tenant_id, role_id, permission_key)
    select r.tenant_id, r.id, p.permission
    from authz.role r
    join (values
      ('app_request_reader', 'service.request_read'),
      ('app_request_filer', 'service.request_read'), ('app_request_filer', 'service.request_on_behalf')
    ) as p(role_key, permission) on p.role_key = r.key
    where r.is_builtin
    on conflict do nothing;
    update authz.grants_version set version = version + 1;
  end loop;
  perform set_config('app.tenant_id', '', true);
end $$;
