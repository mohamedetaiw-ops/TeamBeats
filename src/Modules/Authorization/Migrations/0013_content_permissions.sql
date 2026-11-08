-- Spec 009 (R1): content.author for organization administrators, content editors and content publishers;
-- content.publish_critical and content.admin for tenant administrators
-- only. New tenants get the grants from the seed; this adds them to tenants that already exist, one tenant at a time
-- with the tenant set (see 0007).
insert into authz.permission (key) values ('content.author'), ('content.publish_critical'), ('content.admin') on conflict do nothing;

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
      ('tenant_administrator', 'content.author'), ('tenant_administrator', 'content.publish_critical'), ('tenant_administrator', 'content.admin'),
      ('organization_administrator', 'content.author'),
      ('content_editor', 'content.author'),
      ('content_publisher', 'content.author')
    ) as p(role_key, permission) on p.role_key = r.key
    where r.is_builtin
    on conflict do nothing;
    update authz.grants_version set version = version + 1;
  end loop;
  perform set_config('app.tenant_id', '', true);
end $$;
