-- Spec 005 section 6 and spec 020 FR-019: limits on published and customer-created agents. The package values are
-- [TBD] in spec 005; the defaults follow the Workplace package (the six launch templates, no custom agents) and leave
-- the larger packages unlimited (null) until the commercial values are set.
insert into entitlements.capability (key, type, unit, description_key) values
  ('ai.agents.max_published', 'limit', 'agents', 'capability.ai.agents.max_published'),
  ('ai.agents.max_custom', 'limit', 'agents', 'capability.ai.agents.max_custom')
on conflict (key) do nothing;

insert into entitlements.package_capability (package_version_id, capability_key, enabled, limit_value)
select v.id, c.key, true,
  case when v.package_key = 'workplace' then case c.key when 'ai.agents.max_published' then 6 else 0 end end
from entitlements.package_version v
cross join entitlements.capability c
where c.key in ('ai.agents.max_published', 'ai.agents.max_custom')
on conflict do nothing;
