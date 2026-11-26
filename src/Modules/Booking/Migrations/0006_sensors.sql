-- Spec 031 R3 US9 (FR-026, FR-027): occupancy sensors read through a spec 017 connection and mapped to resources by
-- facilities administrators. Readings carry no personal data: each read adds to the resource's hourly counts, and the
-- sensor keeps its last state. When the tenant turns sensor check-in on, an occupied reading in a booking's check-in
-- window checks it in.
create table booking.sensor (
  tenant_id uuid not null,
  id uuid not null,
  connection_id uuid not null,
  sensor_id text not null check (length(sensor_id) between 1 and 128),
  resource_id uuid not null,
  last_occupied boolean null,
  last_observed_at timestamptz null,
  last_read_at timestamptz null,
  created_by uuid not null,
  created_at timestamptz not null,
  primary key (tenant_id, id),
  unique (tenant_id, connection_id, sensor_id),
  foreign key (tenant_id, resource_id) references booking.resource (tenant_id, id)
);
create index sensor_resource on booking.sensor (tenant_id, resource_id);
select platform.enable_tenant_rls('booking.sensor');
grant select, insert, update, delete on booking.sensor to tb_app;

-- Counts per resource and hour: how many reads there were and how many said occupied. Reads are every 5 minutes, so
-- sensed occupied hours are occupied reads times 5 minutes.
create table booking.occupancy_hour (
  tenant_id uuid not null,
  resource_id uuid not null,
  hour timestamptz not null,
  reads int not null default 0,
  occupied_reads int not null default 0,
  primary key (tenant_id, resource_id, hour),
  foreign key (tenant_id, resource_id) references booking.resource (tenant_id, id)
);
select platform.enable_tenant_rls('booking.occupancy_hour');
grant select, insert, update on booking.occupancy_hour to tb_app;

-- When each connection was last read, so the minute clock reads every 5 minutes.
create table booking.sensor_read (
  tenant_id uuid not null,
  connection_id uuid not null,
  read_at timestamptz not null,
  result text not null,
  primary key (tenant_id, connection_id)
);
select platform.enable_tenant_rls('booking.sensor_read');
grant select, insert, update on booking.sensor_read to tb_app;

alter table booking.setting add column sensor_check_in boolean not null default false;

alter table booking.booking drop constraint booking_check_in_method_check;
alter table booking.booking add constraint booking_check_in_method_check check (check_in_method in ('qr', 'booking', 'kiosk', 'sensor'));
