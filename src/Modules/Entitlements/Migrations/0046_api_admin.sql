-- Spec 035 R3 (T023): admin APIs beside SCIM, role administration through a delegated app, for Enterprise and Government tenants.
insert into entitlements.capability (key, type, unit, description_key) values
  ('api.admin', 'flag', null, 'capability.api.admin')
on conflict (key) do nothing;

insert into entitlements.package_capability (package_version_id, capability_key, enabled, limit_value)
select v.id, 'api.admin', v.package_key = 'enterprise_gov', null
from entitlements.package_version v
on conflict do nothing;
