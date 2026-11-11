-- Spec 037 (R2): the unified calendar. TeamBeats owns only its own events, the answers to them, each person's calendar
-- preferences and the tenant's calendar settings. Meetings, holidays and leave stay with their owners and are read on
-- every request. Every table is tenant-owned with forced row-level security.
create schema calendar;
grant usage on schema calendar to tb_app;

-- A company event, department event or important date (FR-017). org_unit_id is the owning unit (null for the whole
-- tenant) and decides who may manage it with content.publish; audience is the spec 009 model inside that subtree.
-- Timed events keep UTC instants and the IANA zone they were written in; all-day events keep local dates with an
-- exclusive end, so they never shift with a time zone. recurrence is null for a single event.
create table calendar.event (
  tenant_id uuid not null,
  id uuid not null,
  org_unit_id uuid null,
  kind text not null check (kind in ('company', 'department', 'important')),
  titles jsonb not null,
  descriptions jsonb not null default '{}',
  all_day boolean not null,
  start_at timestamptz null,
  end_at timestamptz null,
  start_date date null,
  end_date date null,
  time_zone text not null,
  location text null,
  online_url text null,
  audience jsonb not null default '{}',
  recurrence jsonb null,
  series_end timestamptz null,
  rsvp boolean not null default false,
  status text not null default 'published' check (status in ('published', 'cancelled')),
  cancelled_at timestamptz null,
  created_by uuid not null,
  created_at timestamptz not null,
  updated_at timestamptz not null,
  version int not null default 1,
  primary key (tenant_id, id),
  check ((all_day and start_date is not null and end_date > start_date and start_at is null and end_at is null)
      or (not all_day and start_at is not null and end_at > start_at and start_date is null and end_date is null))
);
create index event_range on calendar.event (tenant_id, status, coalesce(start_at, start_date::timestamptz), series_end);
create index event_unit on calendar.event (tenant_id, org_unit_id);
select platform.enable_tenant_rls('calendar.event');
grant select, insert, update, delete on calendar.event to tb_app;

-- An answer to an event with RSVP on (FR-019), one per person and occurrence. occurrence_key is the occurrence start:
-- the instant for a timed event, the local date for an all-day one.
create table calendar.response (
  tenant_id uuid not null,
  event_id uuid not null,
  occurrence_key text not null,
  user_id uuid not null,
  answer text not null check (answer in ('going', 'maybe', 'declined')),
  answered_at timestamptz not null,
  primary key (tenant_id, event_id, occurrence_key, user_id),
  foreign key (tenant_id, event_id) references calendar.event (tenant_id, id) on delete cascade
);
select platform.enable_tenant_rls('calendar.response');
grant select, insert, update, delete on calendar.response to tb_app;

-- A person's layers and last view (FR-009). Kept per tenant membership, so it follows them to every device.
create table calendar.preference (
  tenant_id uuid not null,
  user_id uuid not null,
  layers text[] not null,
  view text not null check (view in ('agenda', 'week', 'month', 'team')),
  updated_at timestamptz not null,
  primary key (tenant_id, user_id)
);
select platform.enable_tenant_rls('calendar.preference');
grant select, insert, update on calendar.preference to tb_app;

-- Calendar settings (FR-021), versioned: the highest version is in force. Values are checked in code against the
-- settings schema before they are written.
create table calendar.settings (
  tenant_id uuid not null,
  version int not null,
  indirect_reports boolean not null,
  threshold int not null check (threshold between 10 and 100),
  default_layers text[] not null,
  updated_by uuid null,
  updated_at timestamptz not null,
  primary key (tenant_id, version)
);
select platform.enable_tenant_rls('calendar.settings');
grant select, insert on calendar.settings to tb_app;
