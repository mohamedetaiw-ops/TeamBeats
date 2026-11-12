-- Spec 031 section 9: rooms, desks, parking and equipment, and usage reports are Workplace Plus and above.
insert into entitlements.capability (key, type, unit, description_key) values
  ('booking.rooms', 'flag', null, 'capability.booking.rooms'),
  ('booking.desks', 'flag', null, 'capability.booking.desks'),
  ('booking.resources', 'flag', null, 'capability.booking.resources'),
  ('booking.reports', 'flag', null, 'capability.booking.reports')
on conflict (key) do nothing;

insert into entitlements.package_capability (package_version_id, capability_key, enabled, limit_value)
select v.id, c.key, v.package_key <> 'workplace', null
from entitlements.package_version v
cross join entitlements.capability c
where c.key in ('booking.rooms', 'booking.desks', 'booking.resources', 'booking.reports')
on conflict do nothing;
