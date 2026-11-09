-- Specs 020 and 027 (R1): agent.design for agent designers in their org scope, agent.publish for publishers (the AI
-- Policy Administrator by default, spec 020 section 17), agent.view, agent.security_view and agent.suspend for security
-- officers. Adds the agent designer built-in role. New tenants get the role and grants from the seed; this adds them to
-- tenants that already exist, one tenant at a time with the tenant set (see 0007).
insert into authz.permission (key) values ('agent.design'), ('agent.publish'), ('agent.view'), ('agent.security_view'), ('agent.suspend')
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
    select r.tenant_id, gen_random_uuid(), 'agent_designer', true, true, false
    from authz.role r
    where r.key = 'tenant_administrator' and r.is_builtin
    on conflict (tenant_id, key) do nothing;

    insert into authz.role_permission (tenant_id, role_id, permission_key)
    select r.tenant_id, r.id, p.permission
    from authz.role r
    join (values
      ('tenant_administrator', 'agent.design'), ('tenant_administrator', 'agent.publish'), ('tenant_administrator', 'agent.view'),
      ('tenant_administrator', 'agent.security_view'), ('tenant_administrator', 'agent.suspend'),
      ('agent_designer', 'org.view'), ('agent_designer', 'agent.design'), ('agent_designer', 'agent.view'),
      ('ai_policy_administrator', 'agent.publish'), ('ai_policy_administrator', 'agent.view'), ('ai_policy_administrator', 'agent.suspend'),
      ('security_officer', 'agent.view'), ('security_officer', 'agent.security_view'), ('security_officer', 'agent.suspend')
    ) as p(role_key, permission) on p.role_key = r.key
    where r.is_builtin
    on conflict do nothing;
    update authz.grants_version set version = version + 1;
  end loop;
  perform set_config('app.tenant_id', '', true);
end $$;
