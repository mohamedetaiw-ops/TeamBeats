-- Spec 027 section 9 (R2): the Agent Security Center (alerts, incidents, watch list, evidence export) is Enterprise
-- and Government only.
insert into entitlements.capability (key, type, unit, description_key) values
  ('ai.governance.security_center', 'flag', null, 'capability.ai.governance.security_center')
on conflict (key) do nothing;

insert into entitlements.package_capability (package_version_id, capability_key, enabled, limit_value)
select v.id, 'ai.governance.security_center', v.package_key not in ('workplace', 'workplace_plus'), null
from entitlements.package_version v
on conflict do nothing;
