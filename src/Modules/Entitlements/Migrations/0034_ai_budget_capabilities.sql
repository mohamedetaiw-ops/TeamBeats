-- Spec 013 section 9 (R2): the tenant AI budget is in every package; org unit, agent and user budgets and private model
-- endpoints are Workplace Plus and above.
insert into entitlements.capability (key, type, unit, description_key) values
  ('ai.budgets', 'flag', null, 'capability.ai.budgets'),
  ('ai.budgets.scoped', 'flag', null, 'capability.ai.budgets.scoped'),
  ('ai.models.private_endpoint', 'flag', null, 'capability.ai.models.private_endpoint')
on conflict (key) do nothing;

insert into entitlements.package_capability (package_version_id, capability_key, enabled, limit_value)
select v.id, c.key, c.key = 'ai.budgets' or v.package_key <> 'workplace', null
from entitlements.package_version v
cross join (values ('ai.budgets'), ('ai.budgets.scoped'), ('ai.models.private_endpoint')) as c(key)
on conflict do nothing;
