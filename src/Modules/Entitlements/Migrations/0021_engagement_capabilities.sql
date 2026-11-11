-- Spec 023 section 9: communities, recognition and galleries are in every package; Speak Up, surveys and points are
-- Workplace Plus and above. Workplace runs polls, which come with communities, without surveys.
insert into entitlements.capability (key, type, unit, description_key) values
  ('engagement.communities', 'flag', null, 'capability.engagement.communities'),
  ('engagement.speak_up', 'flag', null, 'capability.engagement.speak_up'),
  ('engagement.surveys', 'flag', null, 'capability.engagement.surveys'),
  ('engagement.recognition', 'flag', null, 'capability.engagement.recognition'),
  ('engagement.points', 'flag', null, 'capability.engagement.points'),
  ('engagement.galleries', 'flag', null, 'capability.engagement.galleries')
on conflict (key) do nothing;

insert into entitlements.package_capability (package_version_id, capability_key, enabled, limit_value)
select v.id, c.key,
  case when c.key in ('engagement.communities', 'engagement.recognition', 'engagement.galleries') then true else v.package_key <> 'workplace' end,
  null
from entitlements.package_version v
cross join entitlements.capability c
where c.key in ('engagement.communities', 'engagement.speak_up', 'engagement.surveys', 'engagement.recognition', 'engagement.points', 'engagement.galleries')
on conflict do nothing;
