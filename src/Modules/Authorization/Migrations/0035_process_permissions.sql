-- Spec 024 section 3 (R2): process operators resolve incidents and act on instances, process analysts read monitoring
-- without personal data, and decision owners edit decision tables. Workflow designers own decisions too. New tenants
-- get the roles and grants from the seed; this adds them to tenants that already exist, one tenant at a time (see 0007).
insert into authz.permission (key) values ('process.operate'), ('process.analyze'), ('decision.design')
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
    cross join (values ('process_operator'), ('process_analyst')) as k(key)
    where r.key = 'tenant_administrator' and r.is_builtin
    on conflict (tenant_id, key) do nothing;

    insert into authz.role_permission (tenant_id, role_id, permission_key)
    select r.tenant_id, r.id, p.permission
    from authz.role r
    join (values
      ('tenant_administrator', 'process.operate'), ('tenant_administrator', 'process.analyze'), ('tenant_administrator', 'decision.design'),
      ('workflow_designer', 'decision.design'),
      ('process_operator', 'org.view'), ('process_operator', 'process.operate'), ('process_operator', 'process.analyze'),
      ('process_analyst', 'org.view'), ('process_analyst', 'process.analyze')
    ) as p(role_key, permission) on p.role_key = r.key
    where r.is_builtin
    on conflict do nothing;
    update authz.grants_version set version = version + 1;
  end loop;
  perform set_config('app.tenant_id', '', true);
end $$;
