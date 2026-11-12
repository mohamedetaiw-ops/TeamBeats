-- Spec 029 section 9: project and team spaces and guests in them are Workplace Plus and above; Workplace Plus has up to
-- 200 spaces, Enterprise and Government no limit (null).
insert into entitlements.capability (key, type, unit, description_key) values
  ('spaces.projects', 'flag', null, 'capability.spaces.projects'),
  ('spaces.guests', 'flag', null, 'capability.spaces.guests'),
  ('spaces.max_spaces', 'limit', 'spaces', 'capability.spaces.max_spaces')
on conflict (key) do nothing;

insert into entitlements.package_capability (package_version_id, capability_key, enabled, limit_value)
select v.id, c.key,
  v.package_key <> 'workplace',
  case when c.key = 'spaces.max_spaces' then
    case v.package_key when 'workplace' then 0 when 'workplace_plus' then 200 else null end
  end
from entitlements.package_version v
cross join entitlements.capability c
where c.key in ('spaces.projects', 'spaces.guests', 'spaces.max_spaces')
on conflict do nothing;
