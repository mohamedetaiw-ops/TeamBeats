-- Spec 026 section 9 (R2): choosing a hosting region other than the platform default is in Workplace Plus and above.
insert into entitlements.capability (key, type, unit, description_key) values
  ('deployment.region_choice', 'flag', null, 'capability.deployment.region_choice')
on conflict (key) do nothing;

insert into entitlements.package_capability (package_version_id, capability_key, enabled, limit_value)
select v.id, 'deployment.region_choice', v.package_key <> 'workplace', null
from entitlements.package_version v
on conflict do nothing;
