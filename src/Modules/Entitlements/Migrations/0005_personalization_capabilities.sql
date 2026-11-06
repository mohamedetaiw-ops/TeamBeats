-- Spec 008 section 9: personalization capabilities. Home personalization is in every package; these switch on the
-- parts beyond it. Behavioral personalization is R3 and stays off in R1 whatever the package says.
insert into entitlements.capability (key, type, unit, description_key) values
  ('experience.personalization', 'flag', null, 'capability.experience.personalization'),
  ('experience.audience_variants', 'flag', null, 'capability.experience.audience_variants'),
  ('experience.guided_tours', 'flag', null, 'capability.experience.guided_tours'),
  ('experience.behavioral_personalization', 'flag', null, 'capability.experience.behavioral_personalization')
on conflict (key) do nothing;

insert into entitlements.package_capability (package_version_id, capability_key, enabled, limit_value)
select v.id, c.key,
  case
    when c.key = 'experience.behavioral_personalization' then v.package_key = 'enterprise_gov'
    else v.package_key in ('workplace_plus', 'enterprise_gov')
  end,
  null
from entitlements.package_version v
cross join entitlements.capability c
where c.key in ('experience.personalization', 'experience.audience_variants', 'experience.guided_tours', 'experience.behavioral_personalization')
on conflict do nothing;
