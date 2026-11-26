-- Spec 022 FR-007 (R3, with spec 037 FR-036): a holiday can be provisional until HR confirms its date (for example one
-- that depends on a moon sighting), and can name the date it is observed for when it falls on another day. Both still
-- count as holidays for leave and working days.
alter table hr.calendar_holiday
  add column provisional boolean not null default false,
  add column observed_for date null check (observed_for is null or observed_for <> date);
