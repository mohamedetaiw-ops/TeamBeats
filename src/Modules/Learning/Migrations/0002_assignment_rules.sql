-- Spec 028 slice 3 (US2, FR-005 to FR-008, FR-017): assignment rules, the people each rule gives a course to, and what
-- reminders, overdue notices and recurrence an assignment has had.

-- A rule gives a published course to an audience within the scope of the administrator who made it: every member gets
-- an assignment due due_days after they join it, completions expire after recurrence_months, and an assignment counts as
-- overdue after its due date plus grace_days.
create table learning.assignment_rule (
  tenant_id uuid not null,
  id uuid not null,
  course_id uuid not null,
  org_unit_id uuid null,
  audience jsonb not null,
  people uuid[] not null default '{}',
  due_days int not null check (due_days between 1 and 365),
  recurrence_months int null check (recurrence_months between 1 and 60),
  grace_days int not null default 0 check (grace_days between 0 and 30),
  status text not null default 'active' check (status in ('active', 'ended')),
  created_by uuid not null,
  created_at timestamptz not null,
  ended_by uuid null,
  ended_at timestamptz null,
  evaluated_at timestamptz null,
  primary key (tenant_id, id),
  foreign key (tenant_id, course_id) references learning.course (tenant_id, id)
);
create index assignment_rule_course on learning.assignment_rule (tenant_id, course_id, status);
select platform.enable_tenant_rls('learning.assignment_rule');
grant select, insert, update on learning.assignment_rule to tb_app;

-- Who a rule covers now, with the due date the rule gave them when they joined it. A person covered by several rules has
-- one open assignment with the earliest of these dates (FR-017).
create table learning.rule_target (
  tenant_id uuid not null,
  rule_id uuid not null,
  learner_id uuid not null,
  due_on date not null,
  added_at timestamptz not null,
  primary key (tenant_id, rule_id, learner_id),
  foreign key (tenant_id, rule_id) references learning.assignment_rule (tenant_id, id)
);
create index rule_target_learner on learning.rule_target (tenant_id, learner_id);
select platform.enable_tenant_rls('learning.rule_target');
grant select, insert, update, delete on learning.rule_target to tb_app;

alter table learning.assignment
  add column reminders_sent int[] not null default '{}',
  add column overdue_notified_at timestamptz null,
  add column grace_days int not null default 0,
  add column expires_on date null,
  add column ended_at timestamptz null;
create index assignment_due on learning.assignment (tenant_id, status, due_on);
create index assignment_expiry on learning.assignment (tenant_id, status, expires_on) where expires_on is not null;
grant update on learning.certificate to tb_app;
