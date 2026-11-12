-- Spec 030 section 9: the installable app and push are in every package; offline reading and kiosk devices are
-- Workplace Plus and above; the native shell apps (R3) are Enterprise and Government only.
insert into entitlements.capability (key, type, unit, description_key) values
  ('mobile.pwa', 'flag', null, 'capability.mobile.pwa'),
  ('mobile.push', 'flag', null, 'capability.mobile.push'),
  ('mobile.offline', 'flag', null, 'capability.mobile.offline'),
  ('mobile.kiosk', 'flag', null, 'capability.mobile.kiosk'),
  ('mobile.native_shell', 'flag', null, 'capability.mobile.native_shell')
on conflict (key) do nothing;

insert into entitlements.package_capability (package_version_id, capability_key, enabled, limit_value)
select v.id, c.key,
  case
    when c.key in ('mobile.pwa', 'mobile.push') then true
    when c.key = 'mobile.native_shell' then v.package_key = 'enterprise_gov'
    else v.package_key <> 'workplace'
  end,
  null
from entitlements.package_version v
cross join entitlements.capability c
where c.key in ('mobile.pwa', 'mobile.push', 'mobile.offline', 'mobile.kiosk', 'mobile.native_shell')
on conflict do nothing;
