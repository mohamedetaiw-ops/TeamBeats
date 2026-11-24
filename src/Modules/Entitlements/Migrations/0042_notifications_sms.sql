-- Spec 015 R2 US6: SMS for mandatory messages to frontline staff without email or app, through an SMS gateway
-- connection (spec 017). Enterprise and Government only (spec 015 section 9), metered per calendar month. The limit is a
-- commercial value the spec does not set: 2,000 a month is the default until a contract override changes it.
insert into entitlements.capability (key, type, unit, description_key) values
  ('notifications.sms', 'flag', null, 'capability.notifications.sms'),
  ('notifications.sms_monthly_messages', 'limit', 'messages', 'capability.notifications.sms_monthly_messages')
on conflict (key) do nothing;

insert into entitlements.package_capability (package_version_id, capability_key, enabled, limit_value)
select v.id, 'notifications.sms', v.package_key not in ('workplace', 'workplace_plus'), null
from entitlements.package_version v
on conflict do nothing;

insert into entitlements.package_capability (package_version_id, capability_key, enabled, limit_value)
select v.id, 'notifications.sms_monthly_messages', v.package_key not in ('workplace', 'workplace_plus'),
       case when v.package_key in ('workplace', 'workplace_plus') then null else 2000 end
from entitlements.package_version v
on conflict do nothing;
