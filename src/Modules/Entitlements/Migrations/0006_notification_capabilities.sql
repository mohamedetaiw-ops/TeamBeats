-- Spec 015 section 9 (R1 keys): in-app and email notifications are in every package; a tenant SMTP relay is in
-- Workplace Plus and above. The R2 channel keys are added with their channels.
insert into entitlements.capability (key, type, unit, description_key) values
  ('notifications.core', 'flag', null, 'capability.notifications.core'),
  ('notifications.tenant_smtp', 'flag', null, 'capability.notifications.tenant_smtp')
on conflict (key) do nothing;

insert into entitlements.package_capability (package_version_id, capability_key, enabled, limit_value)
select v.id, c.key,
  case when c.key = 'notifications.core' then true else v.package_key in ('workplace_plus', 'enterprise_gov') end,
  null
from entitlements.package_version v
cross join entitlements.capability c
where c.key in ('notifications.core', 'notifications.tenant_smtp')
on conflict do nothing;
