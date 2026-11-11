-- Spec 022 slice 4 (T020 part 2): correction requests for a day and compensation days for work on a holiday or day
-- off, both approved by the line manager on the process engine (spec 024).

-- The leave type a compensation day is credited to (FR-011). Null means compensation requests are off.
alter table hr.attendance_settings add column compensation_type_key text null;

create table hr.attendance_request (
  tenant_id uuid not null,
  id uuid not null,
  user_id uuid not null,
  org_unit_id uuid null,
  kind text not null check (kind in ('correction', 'compensation')),
  date date not null,
  reason text not null check (char_length(reason) between 1 and 500),
  -- Correction: the punches to add, as [{"time": "HH:mm", "direction": "in" | "out"}]. Compensation: empty.
  punches jsonb not null default '[]',
  -- Compensation: the days to credit (0.5 or 1) and the leave type they go to.
  days numeric(3,1) null check (days is null or days in (0.5, 1)),
  leave_type_id uuid null,
  status text not null check (status in ('pending', 'approved', 'rejected', 'withdrawn')),
  instance_id uuid null,
  decided_by uuid null,
  decided_at timestamptz null,
  created_at timestamptz not null,
  updated_at timestamptz not null,
  primary key (tenant_id, id),
  check ((kind = 'compensation') = (days is not null and leave_type_id is not null))
);
create index attendance_request_user on hr.attendance_request (tenant_id, user_id, date);
-- One open correction per day; one compensation per day that is waiting or credited.
create unique index attendance_request_open_correction on hr.attendance_request (tenant_id, user_id, date) where kind = 'correction' and status = 'pending';
create unique index attendance_request_compensation on hr.attendance_request (tenant_id, user_id, date) where kind = 'compensation' and status in ('pending', 'approved');
select platform.enable_tenant_rls('hr.attendance_request');
grant select, insert, update on hr.attendance_request to tb_app;

-- Punches added by an approved correction keep the request that added them.
alter table hr.attendance_punch add column request_id uuid null;
alter table hr.attendance_punch drop constraint attendance_punch_source_check;
alter table hr.attendance_punch add constraint attendance_punch_source_check check (source in ('web', 'clock', 'correction'));
alter table hr.attendance_punch add constraint attendance_punch_correction check ((source = 'correction') = (request_id is not null));

-- A compensation day is a ledger row of its own reason.
alter table hr.leave_ledger drop constraint leave_ledger_reason_check;
alter table hr.leave_ledger add constraint leave_ledger_reason_check
  check (reason in ('accrual', 'carry_over', 'lapse', 'request', 'cancel', 'adjustment', 'recount', 'compensation'));
