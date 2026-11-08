-- Spec 009 section 9: content core and critical acknowledgement in every package; multi-step approval, campaigns and AI
-- translation from Workplace Plus; AI authoring in every package; sites limited to 3, 25 and unlimited. Keys of R2
-- features are listed so packages are complete; their features refuse until built.
insert into entitlements.capability (key, type, unit, description_key) values
  ('content.core', 'flag', null, 'capability.content.core'),
  ('content.critical_ack', 'flag', null, 'capability.content.critical_ack'),
  ('content.multistep_approval', 'flag', null, 'capability.content.multistep_approval'),
  ('content.max_sites', 'limit', 'sites', 'capability.content.max_sites'),
  ('content.campaigns', 'flag', null, 'capability.content.campaigns'),
  ('content.ai_translation', 'flag', null, 'capability.content.ai_translation'),
  ('ai.authoring', 'flag', null, 'capability.ai.authoring')
on conflict (key) do nothing;

insert into entitlements.package_capability (package_version_id, capability_key, enabled, limit_value)
select v.id, c.key,
  case
    when c.key in ('content.multistep_approval', 'content.campaigns', 'content.ai_translation') then v.package_key <> 'workplace'
    else true
  end,
  case when c.key = 'content.max_sites' then
    case v.package_key when 'workplace' then 3 when 'workplace_plus' then 25 else null end
  end
from entitlements.package_version v
cross join entitlements.capability c
where c.key in ('content.core', 'content.critical_ack', 'content.multistep_approval', 'content.max_sites', 'content.campaigns',
  'content.ai_translation', 'ai.authoring')
on conflict do nothing;
