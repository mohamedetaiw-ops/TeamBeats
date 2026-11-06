-- Spec 008 permissions section: shell, navigation and help permissions. New tenants get them from the seed; this adds
-- them to tenants that already exist. help.edit is reserved for R2 tours so roles stay stable.
insert into authz.permission (key) values ('shell.edit'), ('navigation.edit'), ('help.edit')
on conflict do nothing;

insert into authz.role_permission (tenant_id, role_id, permission_key)
select r.tenant_id, r.id, p.permission
from authz.role r
join (values
  ('tenant_administrator', 'shell.edit'), ('tenant_administrator', 'navigation.edit'), ('tenant_administrator', 'help.edit'),
  ('organization_administrator', 'navigation.edit')
) as p(role_key, permission) on p.role_key = r.key
where r.is_builtin
on conflict do nothing;

update authz.grants_version set version = version + 1;
