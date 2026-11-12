-- Spec 031 (R2) slice 1: sites, floors, zones, bookable resources, booking rules, bookings and recurring series. Every
-- table is tenant-owned with forced row-level security. A booking holds its resource for a time range, and an exclusion
-- constraint lets only one live booking hold any moment of a resource (FR-005), so two parallel attempts cannot both
-- succeed whatever path they take. btree_gist is a trusted contrib extension, so the schema owner creates it here.
create schema booking;
grant usage on schema booking to tb_app;
create extension if not exists btree_gist schema booking;

-- The tenant's booking settings (section 6 defaults). One row per tenant, created on first change.
create table booking.setting (
  tenant_id uuid not null primary key,
  slot_minutes int not null default 15 check (slot_minutes in (5, 10, 15, 30, 60)),
  check_in_before_minutes int not null default 15 check (check_in_before_minutes between 0 and 120),
  check_in_grace_minutes int not null default 15 check (check_in_grace_minutes between 5 and 120),
  approval_hold_hours int not null default 24 check (approval_hold_hours between 1 and 72),
  no_show_limit int not null default 3 check (no_show_limit between 1 and 20),
  no_show_window_days int not null default 30 check (no_show_window_days between 7 and 90),
  no_show_restrict boolean not null default false,
  no_show_restrict_days int not null default 14 check (no_show_restrict_days between 1 and 90),
  no_show_advance_days int not null default 1 check (no_show_advance_days between 0 and 14),
  show_names boolean not null default false,
  version int not null default 1,
  updated_by uuid null,
  updated_at timestamptz not null default now()
);
select platform.enable_tenant_rls('booking.setting');
grant select, insert, update on booking.setting to tb_app;

-- A site (FR-001): an office or campus, optionally tied to a directory location (spec 010). org_unit_id is the scope of
-- the facilities administrators who manage it and the unit whose working calendar (spec 022) decides its working days;
-- null means the whole tenant and the tenant default calendar. Bookings use the site's time zone (FR-017) and, where the
-- rules ask for working hours, its opening hours.
create table booking.site (
  tenant_id uuid not null,
  id uuid not null,
  names jsonb not null,
  location_key text null,
  org_unit_id uuid null,
  time_zone text not null,
  opens_at time not null default '08:00',
  closes_at time not null default '18:00',
  status text not null default 'active' check (status in ('active', 'removed')),
  created_by uuid not null,
  created_at timestamptz not null,
  updated_at timestamptz not null,
  row_version int not null default 1,
  primary key (tenant_id, id),
  check (opens_at < closes_at)
);
select platform.enable_tenant_rls('booking.site');
grant select, insert, update on booking.site to tb_app;

create table booking.floor (
  tenant_id uuid not null,
  id uuid not null,
  site_id uuid not null,
  names jsonb not null,
  sort int not null default 0,
  status text not null default 'active' check (status in ('active', 'removed')),
  primary key (tenant_id, id),
  foreign key (tenant_id, site_id) references booking.site (tenant_id, id)
);
create index floor_site on booking.floor (tenant_id, site_id);
select platform.enable_tenant_rls('booking.floor');
grant select, insert, update on booking.floor to tb_app;

create table booking.zone (
  tenant_id uuid not null,
  id uuid not null,
  floor_id uuid not null,
  names jsonb not null,
  status text not null default 'active' check (status in ('active', 'removed')),
  primary key (tenant_id, id),
  foreign key (tenant_id, floor_id) references booking.floor (tenant_id, id)
);
create index zone_floor on booking.zone (tenant_id, floor_id);
select platform.enable_tenant_rls('booking.zone');
grant select, insert, update on booking.zone to tb_app;

-- A bookable resource (section 2). attributes are tenant-defined facility keys (screen, whiteboard, standing desk,
-- electric charging) that search filters on. A fixed desk belongs to one person and is not offered to others. mailbox
-- is a room's email address, checked against the Microsoft 365 room mailboxes (FR-002). qr_code is an opaque random
-- code printed on the resource; it is unique across tenants and resolves only inside the scanning user's tenant.
create table booking.resource (
  tenant_id uuid not null,
  id uuid not null,
  site_id uuid not null,
  floor_id uuid null,
  zone_id uuid null,
  kind text not null check (kind in ('room', 'desk', 'parking', 'equipment')),
  names jsonb not null,
  capacity int null check (capacity between 1 and 1000),
  attributes text[] not null default '{}',
  fixed_user_id uuid null,
  mailbox text null,
  check_in boolean not null default true,
  needs_approval boolean not null default false,
  qr_code text not null,
  status text not null default 'active' check (status in ('active', 'removed')),
  created_by uuid not null,
  created_at timestamptz not null,
  updated_at timestamptz not null,
  row_version int not null default 1,
  primary key (tenant_id, id),
  foreign key (tenant_id, site_id) references booking.site (tenant_id, id),
  foreign key (tenant_id, floor_id) references booking.floor (tenant_id, id),
  foreign key (tenant_id, zone_id) references booking.zone (tenant_id, id)
);
create index resource_site on booking.resource (tenant_id, site_id, kind) where status = 'active';
create unique index resource_qr on booking.resource (qr_code);
create unique index resource_mailbox on booking.resource (tenant_id, lower(mailbox)) where mailbox is not null and status = 'active';
select platform.enable_tenant_rls('booking.resource');
grant select, insert, update on booking.resource to tb_app;

-- Booking rules (FR-003) for one resource, or for a kind of resource at one site or in the whole tenant. The most
-- specific rule applies: the resource's own, then its kind at its site, then its kind tenant-wide, then the defaults.
-- unit_ids limits who may book to people in those units or below them (section 7); empty means everyone.
create table booking.rule (
  tenant_id uuid not null,
  id uuid not null,
  resource_id uuid null,
  site_id uuid null,
  kind text null check (kind in ('room', 'desk', 'parking', 'equipment')),
  unit_ids uuid[] not null default '{}',
  advance_days int not null check (advance_days between 0 and 365),
  min_minutes int not null check (min_minutes between 5 and 1440),
  max_minutes int not null check (max_minutes between 5 and 20160),
  working_hours boolean not null,
  buffer_minutes int not null default 0 check (buffer_minutes between 0 and 240),
  max_active int not null check (max_active between 1 and 100),
  overnight boolean not null default false,
  series_days int not null default 90 check (series_days between 1 and 365),
  version int not null default 1,
  updated_by uuid not null,
  updated_at timestamptz not null,
  primary key (tenant_id, id),
  check (min_minutes <= max_minutes),
  check ((resource_id is not null and kind is null and site_id is null) or (resource_id is null and kind is not null)),
  foreign key (tenant_id, resource_id) references booking.resource (tenant_id, id),
  foreign key (tenant_id, site_id) references booking.site (tenant_id, id)
);
create unique index rule_resource on booking.rule (tenant_id, resource_id) where resource_id is not null;
create unique index rule_kind on booking.rule (tenant_id, kind, coalesce(site_id, '00000000-0000-0000-0000-000000000000'::uuid)) where resource_id is null;
select platform.enable_tenant_rls('booking.rule');
grant select, insert, update on booking.rule to tb_app;

-- A recurring series (FR-006): occurrences are bookings with the series ID; skipped lists the dates left out with why.
create table booking.series (
  tenant_id uuid not null,
  id uuid not null,
  resource_id uuid not null,
  holder_id uuid not null,
  pattern text not null check (pattern in ('daily', 'weekly')),
  until date not null,
  skipped jsonb not null default '[]',
  created_by uuid not null,
  created_at timestamptz not null,
  primary key (tenant_id, id),
  foreign key (tenant_id, resource_id) references booking.resource (tenant_id, id)
);
select platform.enable_tenant_rls('booking.series');
grant select, insert, update on booking.series to tb_app;

-- A booking. held is the time the resource is taken: the booking plus the buffer of its rule, so the next booking
-- cannot start inside the buffer. Live bookings (tentative, confirmed, checked in) may not overlap on one resource.
-- booked_by differs from holder_id for bookings made on someone's behalf or for a team (US5).
create table booking.booking (
  tenant_id uuid not null,
  id uuid not null,
  resource_id uuid not null,
  holder_id uuid not null,
  booked_by uuid not null,
  series_id uuid null,
  starts_at timestamptz not null,
  ends_at timestamptz not null,
  held tstzrange not null,
  title text null,
  status text not null check (status in ('tentative', 'confirmed', 'checked_in', 'released', 'cancelled', 'rejected', 'expired')),
  checked_in_at timestamptz null,
  check_in_method text null check (check_in_method in ('qr', 'booking', 'kiosk')),
  ended_at timestamptz null,
  end_reason text null,
  created_at timestamptz not null,
  row_version int not null default 1,
  primary key (tenant_id, id),
  check (starts_at < ends_at),
  foreign key (tenant_id, resource_id) references booking.resource (tenant_id, id),
  foreign key (tenant_id, series_id) references booking.series (tenant_id, id),
  constraint booking_no_overlap exclude using gist (tenant_id with =, resource_id with =, held with &&)
    where (status in ('tentative', 'confirmed', 'checked_in'))
);
create index booking_holder on booking.booking (tenant_id, holder_id, starts_at);
create index booking_resource on booking.booking (tenant_id, resource_id, starts_at);
create index booking_live on booking.booking (tenant_id, starts_at) where status in ('tentative', 'confirmed');
select platform.enable_tenant_rls('booking.booking');
grant select, insert, update on booking.booking to tb_app;
