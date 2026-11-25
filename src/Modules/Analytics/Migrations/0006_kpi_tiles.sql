-- Spec 021 R3 (US6 scenario 2, FR-010): the KPI tiles a tenant shows on location dashboards, as versioned configuration
-- (constitution XXIX). Each version holds the whole tile set: metric, names per language and an optional permission.
-- Values are never stored here; they are read through the spec 017 KPI feed contract.
create table analytics.kpi_tile_set (
  tenant_id uuid not null,
  version int not null check (version >= 1),
  schema_version int not null default 1 check (schema_version = 1),
  document jsonb not null,
  changed_at timestamptz not null,
  changed_by uuid not null,
  primary key (tenant_id, version)
);
select platform.enable_tenant_rls('analytics.kpi_tile_set');
grant select, insert on analytics.kpi_tile_set to tb_app;
