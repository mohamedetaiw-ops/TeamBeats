-- Spec 026 US8 (R3): customer-managed encryption keys are for Enterprise and Government tenants.
insert into entitlements.capability (key, type, unit, description_key) values
  ('security.customer_keys', 'flag', null, 'capability.security.customer_keys')
on conflict (key) do nothing;

insert into entitlements.package_capability (package_version_id, capability_key, enabled, limit_value)
select v.id, 'security.customer_keys', v.package_key = 'enterprise_gov', null
from entitlements.package_version v
on conflict do nothing;
