-- Spec 022 R2 slice 2: reduced hours periods on calendars (US2 scenario 3, for example Ramadan) and the HR state of a
-- leave request when the HR system owns balances (FR-006, spec 019 US7). Tenant-owned with forced row-level security.
create table hr.calendar_period (
  tenant_id uuid not null,
  id uuid not null,
  calendar_id uuid not null,
  name jsonb not null,
  start_date date not null,
  end_date date not null,
  hours numeric(4,2) not null check (hours > 0 and hours < 24),
  primary key (tenant_id, id),
  foreign key (tenant_id, calendar_id) references hr.working_calendar (tenant_id, id) on delete cascade,
  check (end_date >= start_date and end_date - start_date < 100)
);
create index calendar_period_calendar on hr.calendar_period (tenant_id, calendar_id, start_date);
select platform.enable_tenant_rls('hr.calendar_period');
grant select, insert, update, delete on hr.calendar_period to tb_app;

-- queued: handed to HR, not confirmed yet; recorded: HR confirmed; failed: HR refused or never answered; unavailable: no HR connection.
alter table hr.leave_request add column hr_state text null check (hr_state in ('queued', 'recorded', 'failed', 'unavailable'));
alter table hr.leave_request add column hr_reference text null;
alter table hr.leave_request add column hr_changes int not null default 0;
