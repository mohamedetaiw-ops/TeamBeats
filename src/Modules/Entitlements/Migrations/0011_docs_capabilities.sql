-- Spec 011 section 9: document core in every package; controlled documents from Workplace Plus. The storage limit per
-- tenant is still to be set in the spec ([TBD]), so no package caps it until it is; the key exists so the upload check
-- is in place. Keys of R2 features are listed so packages are complete; their features refuse until built.
insert into entitlements.capability (key, type, unit, description_key) values
  ('docs.core', 'flag', null, 'capability.docs.core'),
  ('docs.controlled', 'flag', null, 'capability.docs.controlled'),
  ('docs.storage_gb', 'limit', 'gb', 'capability.docs.storage_gb'),
  ('docs.asset_portal', 'flag', null, 'capability.docs.asset_portal'),
  ('records.retention', 'flag', null, 'capability.records.retention'),
  ('records.legal_hold', 'flag', null, 'capability.records.legal_hold'),
  ('docs.view_only_watermark', 'flag', null, 'capability.docs.view_only_watermark'),
  ('docs.guest_sharing', 'flag', null, 'capability.docs.guest_sharing'),
  ('docs.ocr', 'flag', null, 'capability.docs.ocr')
on conflict (key) do nothing;

insert into entitlements.package_capability (package_version_id, capability_key, enabled, limit_value)
select v.id, c.key,
  case
    when c.key in ('docs.core', 'docs.storage_gb') then true
    when c.key = 'records.legal_hold' then v.package_key not in ('workplace', 'workplace_plus')
    else v.package_key <> 'workplace'
  end,
  null
from entitlements.package_version v
cross join entitlements.capability c
where c.key in ('docs.core', 'docs.controlled', 'docs.storage_gb', 'docs.asset_portal', 'records.retention', 'records.legal_hold',
  'docs.view_only_watermark', 'docs.guest_sharing', 'docs.ocr')
on conflict do nothing;
