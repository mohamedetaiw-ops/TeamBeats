-- Spec 016 section 9: connector and webhook steps in service routes are Workplace Plus and above.
insert into entitlements.capability (key, type, unit, description_key) values
  ('workflow.connector_fulfilment', 'flag', null, 'capability.workflow.connector_fulfilment')
on conflict (key) do nothing;

insert into entitlements.package_capability (package_version_id, capability_key, enabled, limit_value)
select v.id, 'workflow.connector_fulfilment', v.package_key <> 'workplace', null
from entitlements.package_version v
on conflict do nothing;
