-- Spec 026 US7 (R2): compliance projects. Compliance officers manage and read them; tenant administrators hold every
-- permission. New tenants get them from the seed; this adds them to tenants that already exist, one tenant at a time
-- (see 0007).
insert into authz.permission (key) values ('compliance.manage'), ('compliance.view')
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
    select r.tenant_id, r.id, p.permission
    from authz.role r
    join (values
      ('tenant_administrator', 'compliance.manage'), ('tenant_administrator', 'compliance.view'),
      ('compliance_officer', 'compliance.manage'), ('compliance_officer', 'compliance.view')
    ) as p(role_key, permission) on p.role_key = r.key
    where r.is_builtin
    on conflict do nothing;
    update authz.grants_version set version = version + 1;
  end loop;
  perform set_config('app.tenant_id', '', true);
end $$;
