-- Spec 008 section 3: personalization permissions. Employees personalize delegated regions; administrators set the
-- policy for their scope. New tenants get them from the seed; this adds them to tenants that already exist.
insert into authz.permission (key) values ('personalization.policy'), ('personal_layout.edit')
on conflict do nothing;

insert into authz.role_permission (tenant_id, role_id, permission_key)
select r.tenant_id, r.id, p.permission
from authz.role r
join (values
  ('tenant_administrator', 'personalization.policy'), ('tenant_administrator', 'personal_layout.edit'),
  ('organization_administrator', 'personalization.policy'),
  ('employee', 'personal_layout.edit')
) as p(role_key, permission) on p.role_key = r.key
where r.is_builtin
on conflict do nothing;

update authz.grants_version set version = version + 1;
