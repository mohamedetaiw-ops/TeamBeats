-- Spec 016 section 9: automatic steps (connector steps in service routes, event and schedule triggers, auto-approve
-- rules) are Workplace Plus and above. Workplace keeps manual submit with approval and task steps.
insert into entitlements.capability (key, type, unit, description_key) values
  ('workflow.automation', 'flag', null, 'capability.workflow.automation')
on conflict (key) do nothing;

insert into entitlements.package_capability (package_version_id, capability_key, enabled, limit_value)
select v.id, 'workflow.automation', v.package_key <> 'workplace', null
from entitlements.package_version v
on conflict do nothing;
