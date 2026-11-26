-- Spec 022 R3 section 9: probation and contract milestones (hr.milestones) and shift rosters (hr.rosters) are Workplace
-- Plus and above.
insert into entitlements.capability (key, type, unit, description_key) values
  ('hr.milestones', 'flag', null, 'capability.hr.milestones'),
  ('hr.rosters', 'flag', null, 'capability.hr.rosters')
on conflict (key) do nothing;

insert into entitlements.package_capability (package_version_id, capability_key, enabled, limit_value)
select v.id, c.key, v.package_key <> 'workplace', null
from entitlements.package_version v
cross join entitlements.capability c
where c.key in ('hr.milestones', 'hr.rosters')
on conflict do nothing;
