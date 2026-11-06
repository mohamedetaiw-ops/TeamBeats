-- Spec 033 section 3: theme and lock permissions and the brand manager built-in role. New tenants get them from the seed;
-- this adds them to tenants that already exist. domain.manage is R2 and not added yet.
insert into authz.permission (key) values ('theme.edit'), ('theme.publish'), ('config_lock.manage')
on conflict do nothing;

insert into authz.role (tenant_id, id, key, is_builtin, is_delegable, is_delegated_admin)
select r.tenant_id, gen_random_uuid(), 'brand_manager', true, true, false
from authz.role r
where r.key = 'tenant_administrator' and r.is_builtin
on conflict (tenant_id, key) do nothing;

insert into authz.role_permission (tenant_id, role_id, permission_key)
select r.tenant_id, r.id, p.permission
from authz.role r
join (values
  ('tenant_administrator', 'theme.edit'), ('tenant_administrator', 'theme.publish'), ('tenant_administrator', 'config_lock.manage'),
  ('organization_administrator', 'theme.edit'), ('organization_administrator', 'theme.publish'),
  ('brand_manager', 'org.view'), ('brand_manager', 'theme.edit')
) as p(role_key, permission) on p.role_key = r.key
where r.is_builtin
on conflict do nothing;

update authz.grants_version set version = version + 1;
