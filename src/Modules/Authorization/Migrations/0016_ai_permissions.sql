-- Spec 013 (R1): ai.policy_view lets security officers read the model policy, route changes and AI usage. New tenants
-- get the grants from the seed; this adds them to tenants that already exist, one tenant at a time with the tenant set
-- (see 0007).
insert into authz.permission (key) values ('ai.policy_view') on conflict do nothing;

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
    select r.tenant_id, r.id, 'ai.policy_view'
    from authz.role r
    where r.is_builtin and r.key in ('tenant_administrator', 'ai_policy_administrator', 'security_officer')
    on conflict do nothing;
    update authz.grants_version set version = version + 1;
  end loop;
  perform set_config('app.tenant_id', '', true);
end $$;
