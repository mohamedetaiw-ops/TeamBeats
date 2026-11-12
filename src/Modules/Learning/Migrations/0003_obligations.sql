-- Spec 028 slice 3 (US4, FR-011): training that an onboarding step (022) or a policy version (025) asked for. One row per
-- person, course and source key; it is done when the person completes the course, cancelled when the source is.
create table learning.obligation (
  tenant_id uuid not null,
  id uuid not null,
  user_id uuid not null,
  course_id uuid not null,
  source text not null check (source in ('onboarding', 'policy')),
  source_key text not null check (length(source_key) between 1 and 200),
  due_on date not null,
  status text not null default 'open' check (status in ('open', 'done', 'cancelled')),
  created_at timestamptz not null,
  done_at timestamptz null,
  primary key (tenant_id, id),
  unique (tenant_id, source_key, user_id, course_id),
  foreign key (tenant_id, course_id) references learning.course (tenant_id, id)
);
create index obligation_user on learning.obligation (tenant_id, user_id, course_id) where status = 'open';
select platform.enable_tenant_rls('learning.obligation');
grant select, insert, update on learning.obligation to tb_app;
