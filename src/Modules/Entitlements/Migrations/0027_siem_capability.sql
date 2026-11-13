-- Spec 026 section 9 (R2): SIEM streaming is Workplace Plus and above.
insert into entitlements.capability (key, type, unit, description_key) values
  ('security.siem', 'flag', null, 'capability.security.siem')
on conflict (key) do nothing;

insert into entitlements.package_capability (package_version_id, capability_key, enabled, limit_value)
select v.id, 'security.siem', v.package_key <> 'workplace', null
from entitlements.package_version v
on conflict do nothing;
