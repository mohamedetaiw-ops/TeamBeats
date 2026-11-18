-- Spec 020 section 9 (R2): customer-created agents and department templates are Workplace Plus and above, the
-- marketplace is in every package, and the advanced templates (R3) are Enterprise and Government only.
insert into entitlements.capability (key, type, unit, description_key) values
  ('ai.agents.custom', 'flag', null, 'capability.ai.agents.custom'),
  ('ai.agents.department', 'flag', null, 'capability.ai.agents.department'),
  ('ai.agents.marketplace', 'flag', null, 'capability.ai.agents.marketplace'),
  ('ai.agents.advanced', 'flag', null, 'capability.ai.agents.advanced')
on conflict (key) do nothing;

insert into entitlements.package_capability (package_version_id, capability_key, enabled, limit_value)
select v.id, c.key,
  case c.key
    when 'ai.agents.marketplace' then true
    when 'ai.agents.advanced' then v.package_key not in ('workplace', 'workplace_plus')
    else v.package_key <> 'workplace'
  end,
  null
from entitlements.package_version v
cross join (values ('ai.agents.custom'), ('ai.agents.department'), ('ai.agents.marketplace'), ('ai.agents.advanced')) as c(key)
on conflict do nothing;
