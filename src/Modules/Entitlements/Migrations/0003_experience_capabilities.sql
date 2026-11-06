-- Spec 032 section 9: experience capabilities. Package values follow the spec table; limits are null for unlimited.
insert into entitlements.capability (key, type, unit, description_key) values
  ('experience.page_builder', 'flag', null, 'capability.experience.page_builder'),
  ('experience.fragments', 'flag', null, 'capability.experience.fragments'),
  ('experience.approval_routes', 'flag', null, 'capability.experience.approval_routes'),
  ('experience.max_custom_templates', 'limit', 'templates', 'capability.experience.max_custom_templates'),
  ('experience.feature_controls', 'flag', null, 'capability.experience.feature_controls'),
  ('experience.custom_components', 'flag', null, 'capability.experience.custom_components')
on conflict (key) do nothing;

insert into entitlements.package_capability (package_version_id, capability_key, enabled, limit_value)
select v.id, c.key,
  case
    when c.key = 'experience.page_builder' then true
    when c.key = 'experience.max_custom_templates' then true
    when c.key = 'experience.custom_components' then v.package_key = 'enterprise_gov'
    else v.package_key in ('workplace_plus', 'enterprise_gov')
  end,
  case when c.key = 'experience.max_custom_templates' then
    case v.package_key when 'workplace' then 5 when 'workplace_plus' then 50 else null end
  end
from entitlements.package_version v
cross join entitlements.capability c
where c.key like 'experience.%'
on conflict do nothing;
