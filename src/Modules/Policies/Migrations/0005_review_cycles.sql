-- Spec 025 US5 (R2): scheduled reviews. Each policy has a review window (days before its review date when the owner
-- gets the review task, 60 by default). policy_review keeps one row per review date: when it opened, when it became
-- overdue, and its outcome with the reviewer and date. A policy has at most one open review.
alter table policies.policy add column review_window_days int not null default 60 check (review_window_days between 1 and 365);

create table policies.policy_review (
  tenant_id uuid not null,
  id uuid not null,
  policy_id uuid not null,
  version int not null,
  due_at timestamptz not null,
  opened_at timestamptz not null,
  overdue_at timestamptz null,
  outcome text null check (outcome in ('no_change', 'minor_update', 'major_revision', 'retire', 'revised')),
  note text null check (note is null or length(note) <= 2000),
  reviewer_user_id uuid null,
  completed_at timestamptz null,
  draft_version_id uuid null,
  next_review_at timestamptz null,
  reattest_asked_at timestamptz null,
  primary key (tenant_id, id),
  unique (tenant_id, policy_id, due_at),
  foreign key (tenant_id, policy_id) references policies.policy (tenant_id, id),
  check ((outcome is null) = (completed_at is null))
);
create unique index policy_review_open on policies.policy_review (tenant_id, policy_id) where completed_at is null;
select platform.enable_tenant_rls('policies.policy_review');
grant select, insert, update on policies.policy_review to tb_app;
