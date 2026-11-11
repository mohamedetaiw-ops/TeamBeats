-- Spec 022 section 9: leave and letters are in every package; attendance, journeys, data change, expenses and AI
-- receipt reading are Workplace Plus and above.
insert into entitlements.capability (key, type, unit, description_key) values
  ('hr.leave', 'flag', null, 'capability.hr.leave'),
  ('hr.attendance', 'flag', null, 'capability.hr.attendance'),
  ('hr.journeys', 'flag', null, 'capability.hr.journeys'),
  ('hr.letters', 'flag', null, 'capability.hr.letters'),
  ('hr.data_change', 'flag', null, 'capability.hr.data_change'),
  ('hr.expenses', 'flag', null, 'capability.hr.expenses'),
  ('ai.receipt_reading', 'flag', null, 'capability.ai.receipt_reading')
on conflict (key) do nothing;

insert into entitlements.package_capability (package_version_id, capability_key, enabled, limit_value)
select v.id, c.key,
  case when c.key in ('hr.leave', 'hr.letters') then true else v.package_key <> 'workplace' end,
  null
from entitlements.package_version v
cross join entitlements.capability c
where c.key in ('hr.leave', 'hr.attendance', 'hr.journeys', 'hr.letters', 'hr.data_change', 'hr.expenses', 'ai.receipt_reading')
on conflict do nothing;
