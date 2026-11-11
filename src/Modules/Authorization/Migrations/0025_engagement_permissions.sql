-- Spec 023 (R2): the engagement permissions and the engagement administrator built-in role. Employees take part,
-- organization administrators moderate, publish surveys and approve recognition in their scope, and engagement
-- administrators hold all five. New tenants get the role and grants from the seed; this adds them to tenants that
-- already exist, one tenant at a time with the tenant set (see 0007).
insert into authz.permission (key) values
  ('engagement.participate'), ('engagement.moderate'), ('engagement.manage'), ('engagement.survey_publish'), ('engagement.recognition_approve')
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
    select r.tenant_id, gen_random_uuid(), 'engagement_administrator', true, true, false
    from authz.role r
    where r.key = 'tenant_administrator' and r.is_builtin
    on conflict (tenant_id, key) do nothing;

    insert into authz.role_permission (tenant_id, role_id, permission_key)
    select r.tenant_id, r.id, p.permission
    from authz.role r
    join (values
      ('tenant_administrator', 'engagement.participate'), ('tenant_administrator', 'engagement.moderate'),
      ('tenant_administrator', 'engagement.manage'), ('tenant_administrator', 'engagement.survey_publish'),
      ('tenant_administrator', 'engagement.recognition_approve'),
      ('employee', 'engagement.participate'),
      ('organization_administrator', 'engagement.moderate'), ('organization_administrator', 'engagement.survey_publish'),
      ('organization_administrator', 'engagement.recognition_approve'),
      ('engagement_administrator', 'org.view'), ('engagement_administrator', 'people.view'),
      ('engagement_administrator', 'engagement.participate'), ('engagement_administrator', 'engagement.moderate'),
      ('engagement_administrator', 'engagement.manage'), ('engagement_administrator', 'engagement.survey_publish'),
      ('engagement_administrator', 'engagement.recognition_approve')
    ) as p(role_key, permission) on p.role_key = r.key
    where r.is_builtin
    on conflict do nothing;
    update authz.grants_version set version = version + 1;
  end loop;
  perform set_config('app.tenant_id', '', true);
end $$;
