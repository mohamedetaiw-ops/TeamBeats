-- Spec 034 (R1): eval.design for agent designers, eval.review for the new Evaluation Reviewer role, eval.policy_manage
-- and eval.view for the AI Policy Administrator, eval.view for security officers. New tenants get them from the seed;
-- this adds them to tenants that already exist, one tenant at a time with the tenant set (see 0007).
insert into authz.permission (key) values ('eval.design'), ('eval.review'), ('eval.policy_manage'), ('eval.view')
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
    select r.tenant_id, gen_random_uuid(), 'evaluation_reviewer', true, true, false
    from authz.role r
    where r.key = 'tenant_administrator' and r.is_builtin
    on conflict (tenant_id, key) do nothing;

    insert into authz.role_permission (tenant_id, role_id, permission_key)
    select r.tenant_id, r.id, p.permission
    from authz.role r
    join (values
      ('tenant_administrator', 'eval.design'), ('tenant_administrator', 'eval.review'), ('tenant_administrator', 'eval.policy_manage'),
      ('tenant_administrator', 'eval.view'),
      ('agent_designer', 'eval.design'),
      ('evaluation_reviewer', 'org.view'), ('evaluation_reviewer', 'eval.review'),
      ('ai_policy_administrator', 'eval.policy_manage'), ('ai_policy_administrator', 'eval.view'),
      ('security_officer', 'eval.view')
    ) as p(role_key, permission) on p.role_key = r.key
    where r.is_builtin
    on conflict do nothing;
    update authz.grants_version set version = version + 1;
  end loop;
  perform set_config('app.tenant_id', '', true);
end $$;
