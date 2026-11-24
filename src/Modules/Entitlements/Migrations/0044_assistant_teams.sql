-- Spec 014 R2 US8: the assistant as a personal chat with the TeamBeats bot in Microsoft Teams. On in every package
-- (spec 014 section 9).
insert into entitlements.capability (key, type, unit, description_key) values
  ('ai.assistant.teams', 'flag', null, 'capability.ai.assistant.teams')
on conflict (key) do nothing;

insert into entitlements.package_capability (package_version_id, capability_key, enabled, limit_value)
select v.id, 'ai.assistant.teams', true, null
from entitlements.package_version v
on conflict do nothing;
