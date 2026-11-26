-- Spec 029 R3 section 9: syncing a space's task list with Planner or Jira (spaces.task_sync) is Workplace Plus and above.
insert into entitlements.capability (key, type, unit, description_key) values
  ('spaces.task_sync', 'flag', null, 'capability.spaces.task_sync')
on conflict (key) do nothing;

insert into entitlements.package_capability (package_version_id, capability_key, enabled, limit_value)
select v.id, c.key, v.package_key <> 'workplace', null
from entitlements.package_version v
cross join entitlements.capability c
where c.key = 'spaces.task_sync'
on conflict do nothing;
