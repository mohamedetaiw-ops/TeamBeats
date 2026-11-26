-- Spec 022 R3 slice 8 (US9, FR-027, FR-028): probation and contract milestones. One row per person and kind, with the
-- due date from HR (spec 019) or set by an HR officer. The markers keep each alert, review task and reminder to once
-- per due date, so a moved date sends them again.
create table hr.milestone (
  tenant_id uuid not null,
  id uuid not null,
  user_id uuid not null,
  kind text not null check (kind in ('probation', 'contract')),
  due_date date not null,
  source text not null check (source in ('hr', 'manual')),
  -- open: waits for the date; reviewed: an extension waits for HR to bring the new date; closed: nothing more to do.
  status text not null check (status in ('open', 'reviewed', 'closed')),
  alert_sent_for date null,
  review_opened_for date null,
  reminder_sent_for date null,
  set_by uuid null,
  created_at timestamptz not null,
  updated_at timestamptz not null,
  primary key (tenant_id, id),
  unique (tenant_id, user_id, kind)
);
create index milestone_due on hr.milestone (tenant_id, status, due_date);
select platform.enable_tenant_rls('hr.milestone');
grant select, insert, update, delete on hr.milestone to tb_app;

-- Probation reviews: the manager's outcome and comment. The comment stays here; only the outcome and a new date go to HR.
create table hr.milestone_review (
  tenant_id uuid not null,
  id uuid not null,
  milestone_id uuid not null,
  user_id uuid not null,
  due_date date not null,
  outcome text not null check (outcome in ('confirm', 'extend', 'end')),
  new_date date null,
  comment text not null check (char_length(comment) between 1 and 2000),
  reviewed_by uuid not null,
  reviewed_at timestamptz not null,
  -- hr: sent to HR (queued until HR answers); recorded or failed once HR answers; none: no connection takes changes.
  write_back text not null check (write_back in ('hr', 'recorded', 'failed', 'none')),
  primary key (tenant_id, id),
  check ((outcome = 'extend') = (new_date is not null))
);
create index milestone_review_of on hr.milestone_review (tenant_id, milestone_id, reviewed_at desc);
select platform.enable_tenant_rls('hr.milestone_review');
grant select, insert, update on hr.milestone_review to tb_app;
