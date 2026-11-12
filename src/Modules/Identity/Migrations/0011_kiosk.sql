-- Spec 030 US4 (R2): kiosk devices, badges and kiosk sessions. A device administrator creates a kiosk for an org unit
-- and a place; the tablet redeems a one-time enrolment code and keeps an opaque device token in a cookie. People sign
-- in there with a badge (an opaque code printed as a QR, or their employee number) and a PIN. Only hashes are stored.
-- Tenant-owned, forced RLS.
create table identity.kiosk_device (
  tenant_id uuid not null,
  id uuid not null,
  name text not null check (length(name) between 1 and 100),
  org_unit_id uuid not null,
  location text null check (location is null or length(location) <= 100),
  features text[] not null default '{}',
  idle_minutes int not null default 2 check (idle_minutes between 1 and 15),
  allow_other_units boolean not null default false,
  enrol_code_hash text null unique,
  enrol_expires_at timestamptz null,
  token_hash text null unique,
  enrolled_at timestamptz null,
  last_seen_at timestamptz null,
  created_by uuid not null,
  created_at timestamptz not null default now(),
  revoked_at timestamptz null,
  revoked_by uuid null,
  version int not null default 1,
  primary key (tenant_id, id)
);
select platform.enable_tenant_rls('identity.kiosk_device');
grant select, insert, update on identity.kiosk_device to tb_app;

-- One badge per person. The code is shown once when issued; failed PINs lock the badge on every kiosk of the tenant.
create table identity.kiosk_badge (
  tenant_id uuid not null,
  user_id uuid not null,
  code_hash text not null unique,
  pin_hash text null,
  pin_set_at timestamptz null,
  failed_count int not null default 0,
  locked_until timestamptz null,
  issued_by uuid not null,
  issued_at timestamptz not null default now(),
  primary key (tenant_id, user_id),
  foreign key (tenant_id, user_id) references identity.user_account (tenant_id, id)
);
select platform.enable_tenant_rls('identity.kiosk_badge');
grant select, insert, update, delete on identity.kiosk_badge to tb_app;

-- A kiosk session belongs to the device it was opened on; revoking the device ends it.
alter table identity.user_session add column kiosk_device_id uuid null;
create index user_session_kiosk on identity.user_session (tenant_id, kiosk_device_id) where kiosk_device_id is not null and revoked_at is null;
