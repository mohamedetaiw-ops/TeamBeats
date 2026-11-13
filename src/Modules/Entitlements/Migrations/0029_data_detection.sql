-- Spec 026 section 9 (R2): sensitive data detection is in Workplace Plus and above.
insert into entitlements.capability (key, type, unit, description_key) values
  ('security.data_detection', 'flag', null, 'capability.security.data_detection')
on conflict (key) do nothing;

insert into entitlements.package_capability (package_version_id, capability_key, enabled, limit_value)
select v.id, 'security.data_detection', v.package_key <> 'workplace', null
from entitlements.package_version v
on conflict do nothing;
