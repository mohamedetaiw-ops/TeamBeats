-- Spec 030 US4 (R2): kiosk sign-in and device management, with the device administrator and kiosk user built-in roles.
-- New tenants get them from the seed; this adds them to tenants that already exist, one tenant at a time (see 0007).
insert into authz.permission (key) values ('kiosk.signin'), ('device.manage')
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
    cross join (values ('device_administrator'), ('kiosk_user')) as k(key)
    where r.key = 'tenant_administrator' and r.is_builtin
    on conflict (tenant_id, key) do nothing;

    insert into authz.role_permission (tenant_id, role_id, permission_key)
    select r.tenant_id, r.id, p.permission
    from authz.role r
    join (values
      ('tenant_administrator', 'kiosk.signin'), ('tenant_administrator', 'device.manage'),
      ('device_administrator', 'org.view'), ('device_administrator', 'people.view'), ('device_administrator', 'device.manage'),
      ('kiosk_user', 'kiosk.signin')
    ) as p(role_key, permission) on p.role_key = r.key
    where r.is_builtin
    on conflict do nothing;
    update authz.grants_version set version = version + 1;
  end loop;
  perform set_config('app.tenant_id', '', true);
end $$;
