-- Spec 033 section 9: brand and white-label capabilities. Limits are null for unlimited.
insert into entitlements.capability (key, type, unit, description_key) values
  ('experience.max_brands', 'limit', 'brands', 'capability.experience.max_brands'),
  ('experience.org_branding', 'flag', null, 'capability.experience.org_branding'),
  ('experience.custom_domain', 'flag', null, 'capability.experience.custom_domain'),
  ('experience.token_extensions', 'flag', null, 'capability.experience.token_extensions')
on conflict (key) do nothing;

insert into entitlements.package_capability (package_version_id, capability_key, enabled, limit_value)
select v.id, c.key,
  case
    when c.key = 'experience.max_brands' then true
    when c.key = 'experience.token_extensions' then v.package_key = 'enterprise_gov'
    else v.package_key in ('workplace_plus', 'enterprise_gov')
  end,
  case when c.key = 'experience.max_brands' then
    case v.package_key when 'workplace' then 1 when 'workplace_plus' then 5 else null end
  end
from entitlements.package_version v
cross join entitlements.capability c
where c.key in ('experience.max_brands', 'experience.org_branding', 'experience.custom_domain', 'experience.token_extensions')
on conflict do nothing;
