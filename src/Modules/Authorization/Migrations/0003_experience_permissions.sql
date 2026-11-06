-- Spec 032 section 3 and FR-048: page and template permissions, and the page editor and page publisher built-in roles.
-- New tenants get them from the seed; this adds them to tenants that already exist.
insert into authz.permission (key) values
  ('page.view'), ('page.edit'), ('page.publish'), ('template.edit'), ('template.distribute'), ('component.enable'),
  ('config_history.view')
on conflict do nothing;

insert into authz.role (tenant_id, id, key, is_builtin, is_delegable, is_delegated_admin)
select r.tenant_id, gen_random_uuid(), k.key, true, true, false
from authz.role r cross join (values ('page_editor'), ('page_publisher')) as k(key)
where r.key = 'tenant_administrator' and r.is_builtin
on conflict (tenant_id, key) do nothing;

insert into authz.role_permission (tenant_id, role_id, permission_key)
select r.tenant_id, r.id, p.permission
from authz.role r
join (values
  ('tenant_administrator', 'page.view'), ('tenant_administrator', 'page.edit'), ('tenant_administrator', 'page.publish'),
  ('tenant_administrator', 'template.edit'), ('tenant_administrator', 'template.distribute'), ('tenant_administrator', 'component.enable'),
  ('tenant_administrator', 'config_history.view'),
  ('organization_administrator', 'page.view'), ('organization_administrator', 'page.edit'), ('organization_administrator', 'page.publish'),
  ('organization_administrator', 'template.edit'),
  ('page_editor', 'org.view'), ('page_editor', 'page.view'), ('page_editor', 'page.edit'),
  ('page_publisher', 'org.view'), ('page_publisher', 'page.view'), ('page_publisher', 'page.edit'), ('page_publisher', 'page.publish'),
  ('security_officer', 'config_history.view'),
  ('employee', 'page.view')
) as p(role_key, permission) on p.role_key = r.key
where r.is_builtin
on conflict do nothing;

update authz.grants_version set version = version + 1;
