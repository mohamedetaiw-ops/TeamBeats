-- Spec 022 R2 slice 1: leave types, leave policies by org subtree, the balance ledger, leave requests and the days
-- each request takes. The balance is the sum of the ledger and is never stored on its own (section 14). Every table is
-- tenant-owned with forced row-level security.

create table hr.leave_type (
  tenant_id uuid not null,
  id uuid not null,
  key text not null check (key ~ '^[a-z][a-z0-9_]{1,39}$'),
  names jsonb not null,
  unit text not null default 'day' check (unit in ('day', 'hour')),
  half_days boolean not null default true,
  paid boolean not null default true,
  sensitive boolean not null default true,
  document_after_days numeric(6,2) null check (document_after_days is null or document_after_days >= 0),
  notice_days int not null default 0 check (notice_days between 0 and 365),
  max_days_per_request numeric(6,2) null check (max_days_per_request is null or max_days_per_request > 0),
  negative_allowed boolean not null default false,
  negative_max numeric(6,2) not null default 0 check (negative_max >= 0),
  membership_types text[] null,
  carry_over_max numeric(6,2) not null default 0 check (carry_over_max >= 0),
  year_start_month smallint not null default 1 check (year_start_month between 1 and 12),
  cancel_needs_approval boolean not null default false,
  active boolean not null default true,
  version int not null default 1,
  created_at timestamptz not null,
  updated_at timestamptz not null,
  updated_by uuid null,
  primary key (tenant_id, id),
  unique (tenant_id, key)
);
select platform.enable_tenant_rls('hr.leave_type');
grant select, insert, update on hr.leave_type to tb_app;

-- One policy per type and org unit (null for the whole tenant); the nearest ancestor's policy applies.
create table hr.leave_policy (
  tenant_id uuid not null,
  id uuid not null,
  leave_type_id uuid not null,
  org_unit_id uuid null,
  bands jsonb not null,
  accrual text not null default 'monthly' check (accrual in ('monthly', 'yearly')),
  version int not null default 1,
  created_at timestamptz not null,
  updated_at timestamptz not null,
  updated_by uuid null,
  primary key (tenant_id, id),
  foreign key (tenant_id, leave_type_id) references hr.leave_type (tenant_id, id)
);
create unique index leave_policy_one_per_unit on hr.leave_policy (tenant_id, leave_type_id, org_unit_id) nulls not distinct;
select platform.enable_tenant_rls('hr.leave_policy');
grant select, insert, update, delete on hr.leave_policy to tb_app;

create table hr.leave_counter (
  tenant_id uuid not null primary key,
  next_number int not null
);
select platform.enable_tenant_rls('hr.leave_counter');
grant select, insert, update on hr.leave_counter to tb_app;

create table hr.leave_request (
  tenant_id uuid not null,
  id uuid not null,
  number text not null,
  user_id uuid not null,
  filed_by uuid not null,
  leave_type_id uuid not null,
  org_unit_id uuid null,
  start_date date not null,
  end_date date not null,
  start_half boolean not null default false,
  end_half boolean not null default false,
  days numeric(7,2) not null,
  status text not null check (status in ('pending', 'approved', 'rejected', 'cancelled', 'withdrawn')),
  comment text null,
  replacement_user_id uuid null,
  replacement_answer text null check (replacement_answer in ('accepted', 'declined')),
  replacement_answered_at timestamptz null,
  document_id uuid null,
  instance_id uuid null,
  change_kind text null check (change_kind in ('cancel', 'shorten')),
  change_end date null,
  change_end_half boolean not null default false,
  change_instance_id uuid null,
  change_count int not null default 0,
  idempotency_key text null,
  decided_by uuid null,
  decided_at timestamptz null,
  created_at timestamptz not null,
  updated_at timestamptz not null,
  primary key (tenant_id, id),
  unique (tenant_id, number),
  foreign key (tenant_id, leave_type_id) references hr.leave_type (tenant_id, id),
  check (end_date >= start_date)
);
create unique index leave_request_idempotency on hr.leave_request (tenant_id, user_id, idempotency_key) where idempotency_key is not null;
create index leave_request_user on hr.leave_request (tenant_id, user_id, start_date);
create index leave_request_replacement on hr.leave_request (tenant_id, replacement_user_id) where replacement_user_id is not null and replacement_answer is null;
select platform.enable_tenant_rls('hr.leave_request');
grant select, insert, update on hr.leave_request to tb_app;

-- The days a request takes, one row per date with the fraction of the day and the leave year it counts against.
create table hr.leave_request_day (
  tenant_id uuid not null,
  request_id uuid not null,
  date date not null,
  fraction numeric(4,2) not null check (fraction > 0 and fraction <= 1),
  leave_year int not null,
  primary key (tenant_id, request_id, date),
  foreign key (tenant_id, request_id) references hr.leave_request (tenant_id, id) on delete cascade
);
create index leave_request_day_date on hr.leave_request_day (tenant_id, date);
select platform.enable_tenant_rls('hr.leave_request_day');
grant select, insert, delete on hr.leave_request_day to tb_app;

create table hr.leave_ledger (
  tenant_id uuid not null,
  id uuid not null,
  user_id uuid not null,
  leave_type_id uuid not null,
  leave_year int not null,
  delta numeric(7,2) not null,
  reason text not null check (reason in ('accrual', 'carry_over', 'lapse', 'request', 'cancel', 'adjustment', 'recount')),
  request_id uuid null,
  period_key text null,
  note text null,
  actor_id uuid null,
  at timestamptz not null,
  primary key (tenant_id, id),
  foreign key (tenant_id, leave_type_id) references hr.leave_type (tenant_id, id)
);
create index leave_ledger_balance on hr.leave_ledger (tenant_id, user_id, leave_type_id, leave_year);
-- Accrual and carry-over run once per person, type and period, however often the job runs.
create unique index leave_ledger_period on hr.leave_ledger (tenant_id, user_id, leave_type_id, period_key) where period_key is not null;
select platform.enable_tenant_rls('hr.leave_ledger');
grant select, insert on hr.leave_ledger to tb_app;

-- A supporting document (for example a sick note): Restricted health data, readable by the person, their approver and
-- HR officers, never sent to AI.
create table hr.leave_document (
  tenant_id uuid not null,
  id uuid not null,
  uploaded_by uuid not null,
  object_key text not null,
  file_name text not null,
  size_bytes bigint not null,
  content_type text not null,
  scan_state text not null check (scan_state in ('clean', 'failed')),
  request_id uuid null,
  created_at timestamptz not null,
  primary key (tenant_id, id)
);
select platform.enable_tenant_rls('hr.leave_document');
grant select, insert, update on hr.leave_document to tb_app;
