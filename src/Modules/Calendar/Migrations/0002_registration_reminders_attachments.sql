-- Spec 037 (R3): registration with seats and a waiting list, reminders and cancellation notices, and attachments.

-- An event takes RSVP answers or registrations, never both (FR-028). capacity is per occurrence. reminder is the offset
-- before each occurrence (FR-032).
alter table calendar.event
  add column registration boolean not null default false,
  add column capacity int null check (capacity between 1 and 10000),
  add column waitlist boolean not null default false,
  add column registration_closes_at timestamptz null,
  add column reminder text not null default '1h' check (reminder in ('none', '15m', '1h', '1d'));
alter table calendar.event add constraint event_answers_check check (not (rsvp and registration) and (not registration or capacity is not null));
update calendar.event set reminder = '1d' where all_day;

-- A seat or a place on the waiting list for one occurrence (FR-028, FR-029). Seats are taken while the event row is
-- locked, so parallel registrations never pass the capacity. joined_at orders the waiting list.
create table calendar.registration (
  tenant_id uuid not null,
  event_id uuid not null,
  occurrence_key text not null,
  user_id uuid not null,
  state text not null check (state in ('registered', 'waiting')),
  joined_at timestamptz not null,
  primary key (tenant_id, event_id, occurrence_key, user_id),
  foreign key (tenant_id, event_id) references calendar.event (tenant_id, id) on delete cascade
);
create index registration_queue on calendar.registration (tenant_id, event_id, occurrence_key, state, joined_at);
select platform.enable_tenant_rls('calendar.registration');
grant select, insert, update, delete on calendar.registration to tb_app;

-- Reminders and cancellation notices already sent (FR-032): one per person, occurrence and kind, written before sending.
create table calendar.reminder_sent (
  tenant_id uuid not null,
  event_id uuid not null,
  occurrence_key text not null,
  user_id uuid not null,
  kind text not null check (kind in ('reminder', 'cancelled')),
  sent_at timestamptz not null,
  primary key (tenant_id, event_id, occurrence_key, user_id, kind),
  foreign key (tenant_id, event_id) references calendar.event (tenant_id, id) on delete cascade
);
select platform.enable_tenant_rls('calendar.reminder_sent');
grant select, insert, delete on calendar.reminder_sent to tb_app;

-- A file attached to an event (FR-033), stored in tenant object storage under calendar/events/{event}/{id}.
create table calendar.attachment (
  tenant_id uuid not null,
  id uuid not null,
  event_id uuid not null,
  name text not null check (char_length(name) between 1 and 200),
  content_type text not null,
  size bigint not null check (size > 0),
  created_by uuid not null,
  created_at timestamptz not null,
  primary key (tenant_id, id),
  foreign key (tenant_id, event_id) references calendar.event (tenant_id, id) on delete cascade
);
create index attachment_event on calendar.attachment (tenant_id, event_id);
select platform.enable_tenant_rls('calendar.attachment');
grant select, insert, delete on calendar.attachment to tb_app;
