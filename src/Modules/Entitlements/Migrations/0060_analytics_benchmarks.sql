-- Spec 021 R3: benchmarks across anonymized tenants that opted in, in Enterprise and Government like the other R3
-- analytics features.
insert into entitlements.capability (key, type, unit, description_key) values
  ('analytics.benchmarks', 'flag', null, 'capability.analytics.benchmarks')
on conflict (key) do nothing;

insert into entitlements.package_capability (package_version_id, capability_key, enabled, limit_value)
select v.id, c.key, v.package_key = 'enterprise_gov', null
from entitlements.package_version v
cross join entitlements.capability c
where c.key = 'analytics.benchmarks'
on conflict do nothing;
