-- Spec 031 R3 US8: booking.reception lets reception see the day's visitors of a site and check them in and out.
-- Tenant administrators hold it, and the Facilities Administrator role carries it for the org unit it is given on. New
-- tenants get both from the seed; this adds them to tenants that already exist in one set-based pass, as in 0042.
insert into authz.permission (key) values ('booking.reception')
on conflict do nothing;

alter table authz.role_permission no force row level security;
alter table authz.role no force row level security;
alter table authz.grants_version no force row level security;

insert into authz.role_permission (tenant_id, role_id, permission_key)
select r.tenant_id, r.id, 'booking.reception'
from authz.role r
where r.is_builtin and r.key in ('tenant_administrator', 'facilities_administrator')
on conflict do nothing;

update authz.grants_version set version = version + 1;

alter table authz.role force row level security;
alter table authz.role_permission force row level security;
alter table authz.grants_version force row level security;
