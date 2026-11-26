-- Spec 014 R3: proactive suggestions in the assistant are in Workplace Plus and Enterprise and Government.
insert into entitlements.capability (key, type, unit, description_key) values
  ('ai.assistant.suggestions', 'flag', null, 'capability.ai.assistant.suggestions')
on conflict (key) do nothing;

insert into entitlements.package_capability (package_version_id, capability_key, enabled, limit_value)
select v.id, c.key, v.package_key in ('workplace_plus', 'enterprise_gov'), null
from entitlements.package_version v
cross join entitlements.capability c
where c.key = 'ai.assistant.suggestions'
on conflict do nothing;
