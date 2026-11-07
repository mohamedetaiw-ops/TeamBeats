-- Spec 010 section 9 (R1 keys): the directory, profiles, org chart, My team and newcomers are in every package;
-- tenant-defined profile fields are in Workplace Plus and above. people.skills arrives with skills in R2.
insert into entitlements.capability (key, type, unit, description_key) values
  ('people.directory', 'flag', null, 'capability.people.directory'),
  ('people.custom_fields', 'flag', null, 'capability.people.custom_fields')
on conflict (key) do nothing;

insert into entitlements.package_capability (package_version_id, capability_key, enabled, limit_value)
select v.id, c.key,
  case when c.key = 'people.directory' then true else v.package_key in ('workplace_plus', 'enterprise_gov') end,
  null
from entitlements.package_version v
cross join entitlements.capability c
where c.key in ('people.directory', 'people.custom_fields')
on conflict do nothing;
