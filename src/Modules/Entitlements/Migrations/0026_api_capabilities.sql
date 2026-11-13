-- Spec 035 section 9: the authorization server, read scopes, the REST API and the MCP server with read tools are in
-- every package; write scopes, event subscriptions and confirmed MCP write tools are Workplace Plus and above. Apps are
-- limited to 2 in Workplace and 20 in Workplace Plus. Monthly call quotas are still to be set with the commercial
-- model (spec 035 section 17), so the limit stays open (null) until then.
insert into entitlements.capability (key, type, unit, description_key) values
  ('api.public', 'flag', null, 'capability.api.public'),
  ('api.write_scopes', 'flag', null, 'capability.api.write_scopes'),
  ('api.event_subscriptions', 'flag', null, 'capability.api.event_subscriptions'),
  ('api.mcp', 'flag', null, 'capability.api.mcp'),
  ('api.mcp_write_tools', 'flag', null, 'capability.api.mcp_write_tools'),
  ('api.max_apps', 'limit', 'apps', 'capability.api.max_apps'),
  ('api.monthly_calls', 'limit', 'calls', 'capability.api.monthly_calls')
on conflict (key) do nothing;

insert into entitlements.package_capability (package_version_id, capability_key, enabled, limit_value)
select v.id, c.key,
  case
    when c.key in ('api.public', 'api.mcp', 'api.max_apps', 'api.monthly_calls') then true
    else v.package_key <> 'workplace'
  end,
  case
    when c.key = 'api.max_apps' and v.package_key = 'workplace' then 2
    when c.key = 'api.max_apps' and v.package_key = 'workplace_plus' then 20
    else null
  end
from entitlements.package_version v
cross join entitlements.capability c
where c.key in ('api.public', 'api.write_scopes', 'api.event_subscriptions', 'api.mcp', 'api.mcp_write_tools', 'api.max_apps', 'api.monthly_calls')
on conflict do nothing;
