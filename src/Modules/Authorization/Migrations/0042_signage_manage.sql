-- Spec 015 US7 (R2): notifications.signage_manage registers and revokes the signage screens of a location. Tenant
-- administrators hold it, and the Location Administrator role carries it for the org unit it is given on. New tenants
-- get both from the seed; this adds them to tenants that already exist, in one set-based pass: the tables stop forcing row security for this transaction only, so the
-- owner writes every tenant's rows at once (a per-tenant loop takes minutes on a large database).
insert into authz.permission (key) values ('notifications.signage_manage')
on conflict do nothing;

alter table authz.role no force row level security;
alter table authz.role_permission no force row level security;
alter table authz.grants_version no force row level security;

insert into authz.role (tenant_id, id, key, is_builtin, is_delegable, is_delegated_admin)
select r.tenant_id, gen_random_uuid(), 'location_administrator', true, true, false
from authz.role r
where r.key = 'tenant_administrator' and r.is_builtin
on conflict (tenant_id, key) do nothing;

insert into authz.role_permission (tenant_id, role_id, permission_key)
select r.tenant_id, r.id, p.permission
from authz.role r
join (values
  ('tenant_administrator', 'notifications.signage_manage'),
  ('location_administrator', 'org.view'), ('location_administrator', 'notifications.signage_manage')) as p(role_key, permission) on p.role_key = r.key
where r.is_builtin
on conflict do nothing;

update authz.grants_version set version = version + 1;

alter table authz.role force row level security;
alter table authz.role_permission force row level security;
alter table authz.grants_version force row level security;
