-- Spec 015 R2 US7: signage screens that show a location's announcements. Off in Workplace, on in Workplace Plus and
-- Enterprise and Government (spec 015 section 9).
insert into entitlements.capability (key, type, unit, description_key) values
  ('notifications.signage', 'flag', null, 'capability.notifications.signage')
on conflict (key) do nothing;

insert into entitlements.package_capability (package_version_id, capability_key, enabled, limit_value)
select v.id, 'notifications.signage', v.package_key <> 'workplace', null
from entitlements.package_version v
on conflict do nothing;
