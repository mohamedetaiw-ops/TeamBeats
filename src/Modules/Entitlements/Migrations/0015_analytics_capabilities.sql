-- Spec 021 section 9: core analytics (adoption basics, item analytics) in every package, and the R2 and R3 keys so
-- packages are complete; their features refuse until built. analytics.advanced and analytics.export exist since 0001.
insert into entitlements.capability (key, type, unit, description_key) values
  ('analytics.core', 'flag', null, 'capability.analytics.core'),
  ('analytics.location_dashboards', 'flag', null, 'capability.analytics.location_dashboards'),
  ('analytics.sentiment', 'flag', null, 'capability.analytics.sentiment'),
  ('analytics.kpi_tiles', 'flag', null, 'capability.analytics.kpi_tiles'),
  ('analytics.api', 'flag', null, 'capability.analytics.api'),
  ('analytics.experience', 'flag', null, 'capability.analytics.experience'),
  ('analytics.experiments', 'flag', null, 'capability.analytics.experiments')
on conflict (key) do nothing;

insert into entitlements.package_capability (package_version_id, capability_key, enabled, limit_value)
select v.id, c.key,
  case
    when c.key = 'analytics.core' then true
    when c.key in ('analytics.kpi_tiles', 'analytics.api', 'analytics.experiments') then v.package_key = 'enterprise_gov'
    else v.package_key <> 'workplace'
  end,
  null
from entitlements.package_version v
cross join entitlements.capability c
where c.key in ('analytics.core', 'analytics.location_dashboards', 'analytics.sentiment', 'analytics.kpi_tiles', 'analytics.api',
  'analytics.experience', 'analytics.experiments')
on conflict do nothing;
