-- Spec 011 (R1): doc.view, doc.contribute, doc.manage for organization administrators; kb.edit for organization
-- administrators, content editors and content publishers; all four for tenant administrators. New tenants get the
-- grants from the seed; this adds them to tenants that already exist, one tenant at a time with the tenant set (see 0007).
insert into authz.permission (key) values ('doc.view'), ('doc.contribute'), ('doc.manage'), ('kb.edit') on conflict do nothing;

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
      ('tenant_administrator', 'doc.view'), ('tenant_administrator', 'doc.contribute'), ('tenant_administrator', 'doc.manage'),
      ('tenant_administrator', 'kb.edit'),
      ('organization_administrator', 'doc.view'), ('organization_administrator', 'doc.contribute'),
      ('organization_administrator', 'doc.manage'), ('organization_administrator', 'kb.edit'),
      ('content_editor', 'kb.edit'),
      ('content_publisher', 'kb.edit')
    ) as p(role_key, permission) on p.role_key = r.key
    where r.is_builtin
    on conflict do nothing;
    update authz.grants_version set version = version + 1;
  end loop;
  perform set_config('app.tenant_id', '', true);
end $$;
