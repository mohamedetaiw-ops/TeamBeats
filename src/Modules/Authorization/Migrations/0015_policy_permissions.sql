-- Spec 025 (R1): policy.author for policy owners and policy.manage for compliance officers, who manage the register,
-- campaigns and the compliance view at tenant or org scope. Adds the compliance officer built-in role. New tenants get
-- the role and grants from the seed; this adds them to tenants that already exist, one tenant at a time with the
-- tenant set (see 0007).
insert into authz.permission (key) values ('policy.author'), ('policy.manage') on conflict do nothing;

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
    select r.tenant_id, gen_random_uuid(), 'compliance_officer', true, true, false
    from authz.role r
    where r.key = 'tenant_administrator' and r.is_builtin
    on conflict (tenant_id, key) do nothing;

    insert into authz.role_permission (tenant_id, role_id, permission_key)
    select r.tenant_id, r.id, p.permission
    from authz.role r
    join (values
      ('tenant_administrator', 'policy.author'), ('tenant_administrator', 'policy.manage'),
      ('compliance_officer', 'org.view'), ('compliance_officer', 'people.view'),
      ('compliance_officer', 'policy.author'), ('compliance_officer', 'policy.manage')
    ) as p(role_key, permission) on p.role_key = r.key
    where r.is_builtin
    on conflict do nothing;
    update authz.grants_version set version = version + 1;
  end loop;
  perform set_config('app.tenant_id', '', true);
end $$;
