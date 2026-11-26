-- Spec 023 R3 section 9: discussion rooms (engagement.rooms) and channel mirroring (engagement.mirroring) are Workplace
-- Plus and above.
insert into entitlements.capability (key, type, unit, description_key) values
  ('engagement.rooms', 'flag', null, 'capability.engagement.rooms'),
  ('engagement.mirroring', 'flag', null, 'capability.engagement.mirroring')
on conflict (key) do nothing;

insert into entitlements.package_capability (package_version_id, capability_key, enabled, limit_value)
select v.id, c.key, v.package_key <> 'workplace', null
from entitlements.package_version v
cross join entitlements.capability c
where c.key in ('engagement.rooms', 'engagement.mirroring')
on conflict do nothing;
