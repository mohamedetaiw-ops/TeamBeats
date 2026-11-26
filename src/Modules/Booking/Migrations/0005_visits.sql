-- Spec 031 R3 US8 (FR-022 to FR-025): a visitor a host expects at a site for a time. The visitor gets an email with an
-- opaque code that works only on the visit day; reception of the site checks them in by code or from the list and the
-- host is told. Thirty days after the visit the visitor's name, email and company and the code are cleared, so only
-- counts remain.
create table booking.visit (
  tenant_id uuid not null,
  id uuid not null,
  site_id uuid not null,
  host_id uuid not null,
  booking_id uuid null,
  day date not null,
  starts_at timestamptz not null,
  ends_at timestamptz not null,
  visitor_name text null check (length(visitor_name) between 1 and 200),
  visitor_email text null check (length(visitor_email) between 3 and 320),
  visitor_company text null check (length(visitor_company) between 1 and 200),
  locale text null,
  code text null check (code ~ '^[A-Z2-9]{8}$'),
  status text not null check (status in ('expected', 'arrived', 'left', 'cancelled')),
  end_reason text null check (end_reason in ('host', 'left_company')),
  arrived_at timestamptz null,
  arrived_by uuid null,
  left_at timestamptz null,
  email_kind text null check (email_kind in ('registered', 'changed', 'cancelled')),
  email_state text not null default 'none' check (email_state in ('none', 'pending', 'sent', 'bounced', 'failed')),
  email_attempts int not null default 0,
  email_next_at timestamptz null,
  purged_at timestamptz null,
  created_at timestamptz not null,
  updated_at timestamptz not null,
  row_version int not null default 1,
  primary key (tenant_id, id),
  foreign key (tenant_id, site_id) references booking.site (tenant_id, id),
  foreign key (tenant_id, booking_id) references booking.booking (tenant_id, id),
  check (ends_at > starts_at),
  check (purged_at is not null or (visitor_name is not null and visitor_email is not null))
);
create unique index visit_code on booking.visit (tenant_id, code) where code is not null;
create index visit_site_day on booking.visit (tenant_id, site_id, day);
create index visit_host on booking.visit (tenant_id, host_id, day);
create index visit_email_due on booking.visit (tenant_id, email_next_at) where email_state = 'pending';
create index visit_purge on booking.visit (tenant_id, ends_at) where purged_at is null;
select platform.enable_tenant_rls('booking.visit');
grant select, insert, update on booking.visit to tb_app;
