-- Spec 017 FR-022: KPI readings received from other systems through signed inbound webhooks with the target "kpi".
-- One row per connection, metric, location and period; a later source time replaces the value. Tenant-owned, forced RLS.
create table integrations.kpi_reading (
  tenant_id uuid not null,
  connection_id uuid not null,
  feed text not null check (feed ~ '^[a-z][a-z0-9_.]{0,63}$'),
  metric text not null check (length(metric) between 1 and 100),
  location text not null default '' check (length(location) <= 100),
  period_start timestamptz not null,
  period_end timestamptz not null check (period_end >= period_start),
  value numeric(20, 6) not null,
  unit text not null check (length(unit) between 1 and 30),
  source_time timestamptz not null,
  received_at timestamptz not null,
  primary key (tenant_id, connection_id, metric, location, period_start, period_end),
  foreign key (tenant_id, connection_id) references integrations.connection (tenant_id, id) on delete cascade
);
create index kpi_reading_metric on integrations.kpi_reading (tenant_id, metric, period_start desc);
select platform.enable_tenant_rls('integrations.kpi_reading');
grant select, insert, update, delete on integrations.kpi_reading to tb_app;
