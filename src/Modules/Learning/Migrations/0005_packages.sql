-- Spec 028 slice 4 (US7, FR-016): SCORM 1.2 and xAPI packages. A package is scanned, then unpacked to tenant storage
-- under learning/packages/{id}/; launch_path is the file that starts it. A package lesson points at one package per
-- language of the course.
create table learning.package (
  tenant_id uuid not null,
  id uuid not null,
  course_id uuid not null,
  kind text not null check (kind in ('scorm12', 'xapi')),
  title text not null check (length(title) between 1 and 200),
  launch_path text not null check (length(launch_path) between 1 and 500),
  activity_id text null check (activity_id is null or length(activity_id) <= 500),
  files int not null,
  bytes bigint not null,
  created_by uuid not null,
  created_at timestamptz not null,
  primary key (tenant_id, id),
  foreign key (tenant_id, course_id) references learning.course (tenant_id, id)
);
create index package_course on learning.package (tenant_id, course_id);
select platform.enable_tenant_rls('learning.package');
grant select, insert on learning.package to tb_app;

-- One launch of a package by a learner from the player. The token in the content address is the only key to the
-- package files and the runtime; only its SHA-256 is kept. state holds the SCORM values the package may read back
-- (suspend data, location) and xAPI state documents.
create table learning.package_launch (
  tenant_id uuid not null,
  id uuid not null,
  token_hash text not null,
  assignment_id uuid not null,
  lesson_key text not null,
  package_id uuid not null,
  course_version int not null,
  state jsonb not null default '{}',
  status text null check (status is null or status in ('completed', 'passed', 'failed', 'incomplete')),
  score int null check (score is null or score between 0 and 100),
  expires_at timestamptz not null,
  created_at timestamptz not null,
  reported_at timestamptz null,
  primary key (tenant_id, id),
  foreign key (tenant_id, assignment_id) references learning.assignment (tenant_id, id),
  foreign key (tenant_id, package_id) references learning.package (tenant_id, id)
);
create unique index package_launch_token on learning.package_launch (token_hash);
create index package_launch_assignment on learning.package_launch (tenant_id, assignment_id, lesson_key);
select platform.enable_tenant_rls('learning.package_launch');
grant select, insert, update on learning.package_launch to tb_app;
