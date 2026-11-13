-- Spec 024 section 9 (R2): process models, decision tables, messages, timers, incidents and monitoring are in Workplace
-- Plus and above; the number of active process model instances is a quota.
insert into entitlements.capability (key, type, unit, description_key) values
  ('workflow.bpmn', 'flag', null, 'capability.workflow.bpmn'),
  ('workflow.max_active_instances', 'limit', 'instances', 'capability.workflow.max_active_instances')
on conflict (key) do nothing;

insert into entitlements.package_capability (package_version_id, capability_key, enabled, limit_value)
select v.id, 'workflow.bpmn', v.package_key <> 'workplace', null
from entitlements.package_version v
on conflict do nothing;

insert into entitlements.package_capability (package_version_id, capability_key, enabled, limit_value)
select v.id, 'workflow.max_active_instances', v.package_key <> 'workplace', case v.package_key when 'workplace_plus' then 10000 else null end
from entitlements.package_version v
on conflict do nothing;
