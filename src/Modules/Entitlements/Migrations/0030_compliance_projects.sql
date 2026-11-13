-- Spec 026 section 9 (R2): compliance projects and the control library are in Enterprise and Government only.
insert into entitlements.capability (key, type, unit, description_key) values
  ('compliance.projects', 'flag', null, 'capability.compliance.projects')
on conflict (key) do nothing;

insert into entitlements.package_capability (package_version_id, capability_key, enabled, limit_value)
select v.id, 'compliance.projects', v.package_key = 'enterprise_gov', null
from entitlements.package_version v
on conflict do nothing;
