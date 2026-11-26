-- Spec 009 R3: sites shared with guest spaces and campaign automation are Enterprise and Government only.
insert into entitlements.capability (key, type, unit, description_key) values
  ('content.guest_sites', 'flag', null, 'capability.content.guest_sites'),
  ('content.campaign_automation', 'flag', null, 'capability.content.campaign_automation')
on conflict (key) do nothing;

insert into entitlements.package_capability (package_version_id, capability_key, enabled, limit_value)
select v.id, c.key, v.package_key = 'enterprise_gov', null
from entitlements.package_version v
cross join entitlements.capability c
where c.key in ('content.guest_sites', 'content.campaign_automation')
on conflict do nothing;
