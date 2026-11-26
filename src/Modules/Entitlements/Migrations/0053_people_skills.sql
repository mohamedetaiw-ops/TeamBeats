-- Spec 010 R2 section 9: skills, endorsements and find an expert (people.skills) are Workplace Plus and above.
insert into entitlements.capability (key, type, unit, description_key) values
  ('people.skills', 'flag', null, 'capability.people.skills')
on conflict (key) do nothing;

insert into entitlements.package_capability (package_version_id, capability_key, enabled, limit_value)
select v.id, c.key, v.package_key <> 'workplace', null
from entitlements.package_version v
cross join entitlements.capability c
where c.key = 'people.skills'
on conflict do nothing;
