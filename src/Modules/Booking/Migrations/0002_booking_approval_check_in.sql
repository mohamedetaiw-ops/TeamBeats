-- Spec 031 (R2) slice 2: restricted resources on a 016 approval route with a tentative hold that expires (FR-007),
-- check-in by QR, from the booking or at a kiosk (FR-008), release of bookings nobody checked in to (FR-009) and the
-- tenant's no-show rule (FR-010).

-- Named approvers of a resource that needs approval. Facilities administrators of the site can always decide too, so a
-- request never waits on an approver who left.
alter table booking.resource add column approver_ids uuid[] not null default '{}';

-- A tentative booking waits for its approval until hold_until: the approval hold of the settings, or the start when
-- that comes first. instance_id is the approval instance; every occurrence of a series shares one. decision_note is
-- the approver's reason when they reject. reminded_at is when the holder was told check-in is open.
alter table booking.booking add column hold_until timestamptz null;
alter table booking.booking add column instance_id uuid null;
alter table booking.booking add column decided_by uuid null;
alter table booking.booking add column decision_note text null;
alter table booking.booking add column reminded_at timestamptz null;
create index booking_tentative on booking.booking (tenant_id, hold_until) where status = 'tentative';
create index booking_instance on booking.booking (tenant_id, instance_id) where instance_id is not null;
create index booking_no_show on booking.booking (tenant_id, holder_id, ended_at) where end_reason = 'no_show';

-- When a person last reached the no-show limit (US4 scenario 3): they are reminded once per window, and while
-- restricted_until is ahead their advance booking window is advance_days.
create table booking.no_show_notice (
  tenant_id uuid not null,
  user_id uuid not null,
  noticed_at timestamptz not null,
  no_shows int not null,
  restricted_until timestamptz null,
  advance_days int null check (advance_days between 0 and 14),
  primary key (tenant_id, user_id)
);
select platform.enable_tenant_rls('booking.no_show_notice');
grant select, insert, update on booking.no_show_notice to tb_app;
