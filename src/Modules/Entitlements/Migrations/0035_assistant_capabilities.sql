-- Spec 014 section 9 (R2): files in a conversation and voice are Workplace Plus and above.
insert into entitlements.capability (key, type, unit, description_key) values
  ('ai.assistant.file_upload', 'flag', null, 'capability.ai.assistant.file_upload'),
  ('ai.assistant.voice', 'flag', null, 'capability.ai.assistant.voice')
on conflict (key) do nothing;

insert into entitlements.package_capability (package_version_id, capability_key, enabled, limit_value)
select v.id, c.key, v.package_key <> 'workplace', null
from entitlements.package_version v
cross join (values ('ai.assistant.file_upload'), ('ai.assistant.voice')) as c(key)
on conflict do nothing;
