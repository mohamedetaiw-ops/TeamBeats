-- Spec 016 section 9 (R1 keys): the catalogue, linked apps, form services, single-step approval, task inbox and
-- delegation are in every package; the number of catalogue services is a quota per package. The spec leaves the quota
-- to commercial data, so the defaults are 25 on Workplace, 200 on Workplace Plus (the design-to scale) and unlimited on
-- Enterprise and Government. Automation and connector fulfilment keys arrive with their features in R2.
insert into entitlements.capability (key, type, unit, description_key) values
  ('workflow.catalogue', 'flag', null, 'capability.workflow.catalogue'),
  ('workflow.max_services', 'limit', 'services', 'capability.workflow.max_services')
on conflict (key) do nothing;

insert into entitlements.package_capability (package_version_id, capability_key, enabled, limit_value)
select v.id, c.key, true,
  case when c.key = 'workflow.max_services' then
    case v.package_key when 'workplace' then 25 when 'workplace_plus' then 200 else null end
  end
from entitlements.package_version v
cross join entitlements.capability c
where c.key in ('workflow.catalogue', 'workflow.max_services')
on conflict do nothing;
