-- Spec 032 R2 T030: feature_control.manage sets experience feature controls for the tenant. Tenant administrators hold it.
-- New tenants get the grant from the seed; this adds it to tenants that already exist, one tenant at a time (see 0007).
insert into authz.permission (key) values ('feature_control.manage')
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
    insert into authz.role_permission (tenant_id, role_id, permission_key)
    select r.tenant_id, r.id, 'feature_control.manage'
    from authz.role r
    where r.is_builtin and r.key = 'tenant_administrator'
    on conflict do nothing;
    update authz.grants_version set version = version + 1;
  end loop;
  perform set_config('app.tenant_id', '', true);
end $$;
