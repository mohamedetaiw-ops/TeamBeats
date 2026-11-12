-- Spec 028 (R2): the learning permissions and the learning administrator built-in role. Employees take courses,
-- organization administrators build, assign and report on courses in their scope, compliance officers report and link
-- courses to policies, and learning administrators hold take, manage and report. New tenants get the role and grants
-- from the seed; this adds them to tenants that already exist, one tenant at a time with the tenant set (see 0007).
insert into authz.permission (key) values
  ('learning.take'), ('learning.manage'), ('learning.team_view'), ('learning.report'), ('learning.assign_linked')
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
    select r.tenant_id, gen_random_uuid(), 'learning_administrator', true, true, false
    from authz.role r
    where r.key = 'tenant_administrator' and r.is_builtin
    on conflict (tenant_id, key) do nothing;

    insert into authz.role_permission (tenant_id, role_id, permission_key)
    select r.tenant_id, r.id, p.permission
    from authz.role r
    join (values
      ('tenant_administrator', 'learning.take'), ('tenant_administrator', 'learning.manage'), ('tenant_administrator', 'learning.team_view'),
      ('tenant_administrator', 'learning.report'), ('tenant_administrator', 'learning.assign_linked'),
      ('employee', 'learning.take'),
      ('organization_administrator', 'learning.manage'), ('organization_administrator', 'learning.report'),
      ('compliance_officer', 'learning.report'), ('compliance_officer', 'learning.assign_linked'),
      ('learning_administrator', 'org.view'), ('learning_administrator', 'people.view'),
      ('learning_administrator', 'learning.take'), ('learning_administrator', 'learning.manage'), ('learning_administrator', 'learning.report')
    ) as p(role_key, permission) on p.role_key = r.key
    where r.is_builtin
    on conflict do nothing;
    update authz.grants_version set version = version + 1;
  end loop;
  perform set_config('app.tenant_id', '', true);
end $$;
