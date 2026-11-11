-- Spec 019 section 9: HR sync, payslips and HR form prefill are Workplace Plus and above. The named Oracle HCM and
-- SAP SuccessFactors packs (R3) are Enterprise & Government only.
insert into entitlements.capability (key, type, unit, description_key) values
  ('hr.integration', 'flag', null, 'capability.hr.integration'),
  ('hr.payslips', 'flag', null, 'capability.hr.payslips'),
  ('hr.form_prefill', 'flag', null, 'capability.hr.form_prefill'),
  ('hr.named_packs', 'flag', null, 'capability.hr.named_packs')
on conflict (key) do nothing;

insert into entitlements.package_capability (package_version_id, capability_key, enabled, limit_value)
select v.id, c.key,
  case when c.key = 'hr.named_packs' then v.package_key not in ('workplace', 'workplace_plus') else v.package_key <> 'workplace' end,
  null
from entitlements.package_version v
cross join entitlements.capability c
where c.key in ('hr.integration', 'hr.payslips', 'hr.form_prefill', 'hr.named_packs')
on conflict do nothing;
