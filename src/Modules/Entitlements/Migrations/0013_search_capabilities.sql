-- Spec 012 section 9: keyword and semantic search and saved search alerts in every package; federated search and the
-- AI summary from Workplace Plus. Keys of R2 features are listed so packages are complete; their features refuse until
-- built.
insert into entitlements.capability (key, type, unit, description_key) values
  ('search.core', 'flag', null, 'capability.search.core'),
  ('search.semantic', 'flag', null, 'capability.search.semantic'),
  ('search.federated', 'flag', null, 'capability.search.federated'),
  ('search.ai_summary', 'flag', null, 'capability.search.ai_summary'),
  ('search.saved_alerts', 'flag', null, 'capability.search.saved_alerts')
on conflict (key) do nothing;

insert into entitlements.package_capability (package_version_id, capability_key, enabled, limit_value)
select v.id, c.key,
  case
    when c.key in ('search.federated', 'search.ai_summary') then v.package_key <> 'workplace'
    else true
  end,
  null
from entitlements.package_version v
cross join entitlements.capability c
where c.key in ('search.core', 'search.semantic', 'search.federated', 'search.ai_summary', 'search.saved_alerts')
on conflict do nothing;
