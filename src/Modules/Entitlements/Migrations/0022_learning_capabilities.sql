-- Spec 028 section 9: courses, mandatory training and the LMS connector are Workplace Plus and above; SCORM and xAPI
-- import are Enterprise and Government only.
insert into entitlements.capability (key, type, unit, description_key) values
  ('learning.courses', 'flag', null, 'capability.learning.courses'),
  ('learning.mandatory', 'flag', null, 'capability.learning.mandatory'),
  ('learning.scorm', 'flag', null, 'capability.learning.scorm'),
  ('learning.lms_connector', 'flag', null, 'capability.learning.lms_connector')
on conflict (key) do nothing;

insert into entitlements.package_capability (package_version_id, capability_key, enabled, limit_value)
select v.id, c.key,
  case when c.key = 'learning.scorm' then v.package_key not in ('workplace', 'workplace_plus') else v.package_key <> 'workplace' end,
  null
from entitlements.package_version v
cross join entitlements.capability c
where c.key in ('learning.courses', 'learning.mandatory', 'learning.scorm', 'learning.lms_connector')
on conflict do nothing;
