-- Spec 026 section 9 (R2): how many spaces may have guests. Workplace 0 (guest access off), Workplace Plus 5,
-- Enterprise and Government no limit (null).
insert into entitlements.capability (key, type, unit, description_key) values
  ('security.max_guest_spaces', 'limit', 'spaces', 'capability.security.max_guest_spaces')
on conflict (key) do nothing;

insert into entitlements.package_capability (package_version_id, capability_key, enabled, limit_value)
select v.id, 'security.max_guest_spaces', true,
  case v.package_key when 'workplace' then 0 when 'workplace_plus' then 5 else null end
from entitlements.package_version v
on conflict do nothing;
