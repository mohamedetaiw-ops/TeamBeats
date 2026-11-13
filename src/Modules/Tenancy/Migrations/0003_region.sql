-- Spec 026 FR-008: the hosting region chosen at tenant creation. Null is the platform default region of an older tenant.
alter table tenancy.tenant_settings add column region text null check (region is null or region ~ '^[a-z][a-z0-9-]{1,30}$');
