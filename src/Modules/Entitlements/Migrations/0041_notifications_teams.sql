-- Spec 015 R2 US5: notifications in the Microsoft Teams activity feed, through the Microsoft 365 connection (spec 006).
-- On in every package (spec 015 section 9).
insert into entitlements.capability (key, type, unit, description_key) values
  ('notifications.teams', 'flag', null, 'capability.notifications.teams')
on conflict (key) do nothing;

insert into entitlements.package_capability (package_version_id, capability_key, enabled, limit_value)
select v.id, 'notifications.teams', true, null
from entitlements.package_version v
on conflict do nothing;
