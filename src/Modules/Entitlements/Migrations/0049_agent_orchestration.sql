-- Spec 020 R3 (US10, FR-016): handoffs between agents. Workplace Plus allows the handoffs platform templates declare;
-- Enterprise and Government also those a tenant adds to its own agents.
insert into entitlements.capability (key, type, unit, description_key) values
  ('ai.agents.orchestration', 'flag', null, 'capability.ai.agents.orchestration'),
  ('ai.agents.orchestration_custom', 'flag', null, 'capability.ai.agents.orchestration_custom')
on conflict (key) do nothing;

insert into entitlements.package_capability (package_version_id, capability_key, enabled, limit_value)
select v.id, 'ai.agents.orchestration', v.package_key in ('workplace_plus', 'enterprise_gov'), null from entitlements.package_version v
on conflict do nothing;

insert into entitlements.package_capability (package_version_id, capability_key, enabled, limit_value)
select v.id, 'ai.agents.orchestration_custom', v.package_key = 'enterprise_gov', null from entitlements.package_version v
on conflict do nothing;
