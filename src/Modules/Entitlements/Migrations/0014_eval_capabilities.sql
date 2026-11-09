-- Spec 034 section 9: the evaluation baseline is in every package with no key. Advanced evaluation (custom rubrics,
-- weighted scores, organization datasets, cross-model comparisons, judge choice), production evaluation and business
-- outcomes from Workplace Plus; experiments and trace export in Enterprise & Gov. R2 keys are listed so packages are
-- complete; their features refuse until built.
insert into entitlements.capability (key, type, unit, description_key) values
  ('ai.evals.advanced', 'flag', null, 'capability.ai.evals.advanced'),
  ('ai.evals.production', 'flag', null, 'capability.ai.evals.production'),
  ('ai.evals.experiments', 'flag', null, 'capability.ai.evals.experiments'),
  ('ai.evals.business_outcomes', 'flag', null, 'capability.ai.evals.business_outcomes'),
  ('ai.evals.trace_export', 'flag', null, 'capability.ai.evals.trace_export')
on conflict (key) do nothing;

insert into entitlements.package_capability (package_version_id, capability_key, enabled, limit_value)
select v.id, c.key,
  case
    when c.key in ('ai.evals.experiments', 'ai.evals.trace_export') then v.package_key = 'enterprise_gov'
    else v.package_key <> 'workplace'
  end,
  null
from entitlements.package_version v
cross join entitlements.capability c
where c.key in ('ai.evals.advanced', 'ai.evals.production', 'ai.evals.experiments', 'ai.evals.business_outcomes', 'ai.evals.trace_export')
on conflict do nothing;
