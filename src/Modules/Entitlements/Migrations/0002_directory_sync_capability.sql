-- Spec 003 section 9: directory sync is part of every package. Added to the catalogue for the R1 directory
-- connection features; existing package versions gain the key with the value the spec fixes.
insert into entitlements.capability (key, type, unit, description_key) values
  ('identity.directory_sync', 'flag', null, 'capability.identity.directory_sync')
on conflict (key) do nothing;
insert into entitlements.package_capability (package_version_id, capability_key, enabled, limit_value)
select id, 'identity.directory_sync', true, null from entitlements.package_version
on conflict do nothing;
