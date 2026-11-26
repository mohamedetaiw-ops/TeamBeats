-- Spec 027 R3: external MCP servers as agent tool sources and L4 bounded autonomy, for Enterprise and Government tenants.
insert into entitlements.capability (key, type, unit, description_key) values
  ('ai.agents.mcp', 'flag', null, 'capability.ai.agents.mcp'),
  ('ai.agents.autonomous', 'flag', null, 'capability.ai.agents.autonomous')
on conflict (key) do nothing;

insert into entitlements.package_capability (package_version_id, capability_key, enabled, limit_value)
select v.id, c.key, v.package_key = 'enterprise_gov', null
from entitlements.package_version v cross join (values ('ai.agents.mcp'), ('ai.agents.autonomous')) as c(key)
on conflict do nothing;
