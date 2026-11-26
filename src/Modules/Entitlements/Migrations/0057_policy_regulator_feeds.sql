-- Spec 025 R3 US10: regulator change feeds are Enterprise and Government only.
insert into entitlements.capability (key, type, unit, description_key) values
  ('policy.regulator_feeds', 'flag', null, 'capability.policy.regulator_feeds')
on conflict (key) do nothing;

insert into entitlements.package_capability (package_version_id, capability_key, enabled, limit_value)
select v.id, 'policy.regulator_feeds', v.package_key = 'enterprise_gov', null
from entitlements.package_version v
on conflict do nothing;
