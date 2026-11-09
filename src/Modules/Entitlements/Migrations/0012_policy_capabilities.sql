-- Spec 025 section 9: the policy register, approval, publishing and one open attestation campaign at a time in every
-- package; unlimited campaigns, comprehension checks and exemptions from Workplace Plus; control mapping and evidence
-- packs in Enterprise and Government; policy Q&A in every package. Keys of R2 features are listed so packages are
-- complete; their features refuse until built.
insert into entitlements.capability (key, type, unit, description_key) values
  ('policy.core', 'flag', null, 'capability.policy.core'),
  ('policy.campaigns', 'flag', null, 'capability.policy.campaigns'),
  ('policy.comprehension', 'flag', null, 'capability.policy.comprehension'),
  ('policy.exemptions', 'flag', null, 'capability.policy.exemptions'),
  ('policy.evidence', 'flag', null, 'capability.policy.evidence'),
  ('ai.policy_qa', 'flag', null, 'capability.ai.policy_qa')
on conflict (key) do nothing;

insert into entitlements.package_capability (package_version_id, capability_key, enabled, limit_value)
select v.id, c.key,
  case
    when c.key in ('policy.core', 'ai.policy_qa') then true
    when c.key = 'policy.evidence' then v.package_key not in ('workplace', 'workplace_plus')
    else v.package_key <> 'workplace'
  end,
  null
from entitlements.package_version v
cross join entitlements.capability c
where c.key in ('policy.core', 'policy.campaigns', 'policy.comprehension', 'policy.exemptions', 'policy.evidence', 'ai.policy_qa')
on conflict do nothing;
