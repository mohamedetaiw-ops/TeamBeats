-- Spec 026 section 9 (R1 keys): tenant-wide IP rules, session policy with MFA assurance and basic alerts are in every
-- package; admin area and per role network rules and advanced alerts are in Workplace Plus and above. Guest, SIEM,
-- detection, key, four-eyes, compliance and region keys arrive with their features in R2 and R3.
insert into entitlements.capability (key, type, unit, description_key) values
  ('security.ip_rules', 'flag', null, 'capability.security.ip_rules'),
  ('security.ip_rules_scoped', 'flag', null, 'capability.security.ip_rules_scoped'),
  ('security.session_policy', 'flag', null, 'capability.security.session_policy'),
  ('security.alerts', 'flag', null, 'capability.security.alerts'),
  ('security.alerts_advanced', 'flag', null, 'capability.security.alerts_advanced')
on conflict (key) do nothing;

insert into entitlements.package_capability (package_version_id, capability_key, enabled, limit_value)
select v.id, c.key,
  case when c.key in ('security.ip_rules', 'security.session_policy', 'security.alerts') then true
       else v.package_key in ('workplace_plus', 'enterprise_gov') end,
  null
from entitlements.package_version v
cross join entitlements.capability c
where c.key in ('security.ip_rules', 'security.ip_rules_scoped', 'security.session_policy', 'security.alerts', 'security.alerts_advanced')
on conflict do nothing;
