-- Spec 017 section 9: the connector framework and its parts. Workplace has none of them; Workplace Plus has ten
-- connections; Enterprise & Government has no limit (null). Oracle and SAP keep their own keys from 0001.
insert into entitlements.capability (key, type, unit, description_key) values
  ('integration.connectors', 'flag', null, 'capability.integration.connectors'),
  ('integration.custom_rest', 'flag', null, 'capability.integration.custom_rest'),
  ('integration.webhooks', 'flag', null, 'capability.integration.webhooks'),
  ('integration.onprem_agent', 'flag', null, 'capability.integration.onprem_agent'),
  ('integration.max_connections', 'limit', 'connections', 'capability.integration.max_connections')
on conflict (key) do nothing;

insert into entitlements.package_capability (package_version_id, capability_key, enabled, limit_value)
select v.id, c.key,
  case when c.key = 'integration.max_connections' then true else v.package_key <> 'workplace' end,
  case when c.key = 'integration.max_connections' then
    case v.package_key when 'workplace' then 0 when 'workplace_plus' then 10 end
  end
from entitlements.package_version v
cross join entitlements.capability c
where c.key in ('integration.connectors', 'integration.custom_rest', 'integration.webhooks', 'integration.onprem_agent',
  'integration.max_connections')
on conflict do nothing;
