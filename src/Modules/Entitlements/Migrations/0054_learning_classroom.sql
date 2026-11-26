-- Spec 028 R3 section 9: classroom sessions with seats and attendance (learning.classroom) are Workplace Plus and above.
insert into entitlements.capability (key, type, unit, description_key) values
  ('learning.classroom', 'flag', null, 'capability.learning.classroom')
on conflict (key) do nothing;

insert into entitlements.package_capability (package_version_id, capability_key, enabled, limit_value)
select v.id, c.key, v.package_key <> 'workplace', null
from entitlements.package_version v
cross join entitlements.capability c
where c.key = 'learning.classroom'
on conflict do nothing;
