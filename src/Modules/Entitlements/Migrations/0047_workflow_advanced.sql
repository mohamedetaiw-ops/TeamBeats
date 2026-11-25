-- Spec 024 R3: compensation, instance migration and BPMN import from other tools, for Enterprise and Government tenants.
insert into entitlements.capability (key, type, unit, description_key) values
  ('workflow.bpmn_advanced', 'flag', null, 'capability.workflow.bpmn_advanced')
on conflict (key) do nothing;

insert into entitlements.package_capability (package_version_id, capability_key, enabled, limit_value)
select v.id, 'workflow.bpmn_advanced', v.package_key = 'enterprise_gov', null
from entitlements.package_version v
on conflict do nothing;
