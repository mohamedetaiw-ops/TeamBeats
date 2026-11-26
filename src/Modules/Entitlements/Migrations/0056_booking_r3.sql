-- Spec 031 R3 section 9: floor plan maps and visitor booking are Workplace Plus and above; sensor occupancy is
-- Enterprise and Government only.
insert into entitlements.capability (key, type, unit, description_key) values
  ('booking.floor_plans', 'flag', null, 'capability.booking.floor_plans'),
  ('booking.visitors', 'flag', null, 'capability.booking.visitors'),
  ('booking.sensors', 'flag', null, 'capability.booking.sensors')
on conflict (key) do nothing;

insert into entitlements.package_capability (package_version_id, capability_key, enabled, limit_value)
select v.id, c.key,
  case when c.key = 'booking.sensors' then v.package_key = 'enterprise_gov' else v.package_key <> 'workplace' end,
  null
from entitlements.package_version v
cross join entitlements.capability c
where c.key in ('booking.floor_plans', 'booking.visitors', 'booking.sensors')
on conflict do nothing;
