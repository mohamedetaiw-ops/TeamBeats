-- Spec 019 (R2): hr.integration_manage (HR sync results, unmatched people, held runs) for tenant administrators and HR officers. New tenants get
-- it from the seed; this adds it to tenants that already exist, one tenant at a time with the tenant set (see 0007).
insert into authz.permission (key) values ('hr.integration_manage') on conflict do nothing;

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
    select r.tenant_id, r.id, 'hr.integration_manage'
    from authz.role r
    where r.key in ('tenant_administrator', 'hr_officer') and r.is_builtin
    on conflict do nothing;
    update authz.grants_version set version = version + 1;
  end loop;
  perform set_config('app.tenant_id', '', true);
end $$;
