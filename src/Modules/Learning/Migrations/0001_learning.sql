-- Spec 028 (R2) slice 1: courses and their published versions, learning paths, and each learner's assignment with lesson
-- progress, quiz attempts, extra attempts and certificates. Every table is tenant-owned with forced row-level security.
create schema learning;
grant usage on schema learning to tb_app;

-- A course (FR-001). draft is the definition being built: titles and summaries per locale, lessons that reference
-- pages, documents and videos by ID, and quizzes. Publishing copies it to a new course_version; editing a published
-- course changes only the draft. org_unit_id is the scope of the learning administrators who may manage it (null for
-- the whole tenant). The audience decides who sees it in the catalogue (FR-018); open_enrolment lets them start it on
-- their own. retraining_version is the latest version published as "requires retraining" (FR-012).
create table learning.course (
  tenant_id uuid not null,
  id uuid not null,
  org_unit_id uuid null,
  category text null,
  status text not null default 'draft' check (status in ('draft', 'published', 'retired')),
  draft jsonb not null,
  draft_revision int not null default 1,
  published_revision int null,
  published_version int null,
  retraining_version int null,
  audience jsonb not null default '{}',
  open_enrolment boolean not null default false,
  created_by uuid not null,
  created_at timestamptz not null,
  updated_at timestamptz not null,
  primary key (tenant_id, id)
);
create index course_status on learning.course (tenant_id, status);
select platform.enable_tenant_rls('learning.course');
grant select, insert, update on learning.course to tb_app;

-- A published version. sources holds the version of each referenced page and document when it was published, so a
-- later change to a source shows as "changed since publishing" until the course is published again.
create table learning.course_version (
  tenant_id uuid not null,
  course_id uuid not null,
  version int not null,
  definition jsonb not null,
  sources jsonb not null default '{}',
  requires_retraining boolean not null default false,
  published_by uuid not null,
  published_at timestamptz not null,
  primary key (tenant_id, course_id, version),
  foreign key (tenant_id, course_id) references learning.course (tenant_id, id)
);
select platform.enable_tenant_rls('learning.course_version');
grant select, insert on learning.course_version to tb_app;

-- A learner's assignment of a course, from self enrolment now and from rules, managers, onboarding and policies in
-- later slices. One open assignment per learner and course; a new cycle starts after a completion stops counting.
-- last_lesson_key is where the learner resumes on any device (FR-009).
create table learning.assignment (
  tenant_id uuid not null,
  id uuid not null,
  course_id uuid not null,
  learner_id uuid not null,
  source text not null check (source in ('self', 'rule', 'manager', 'onboarding', 'policy')),
  status text not null default 'open' check (status in ('open', 'completed', 'withdrawn', 'expired')),
  due_on date null,
  cycle int not null default 1,
  last_lesson_key text null,
  started_at timestamptz null,
  completed_at timestamptz null,
  completed_version int null,
  created_at timestamptz not null,
  primary key (tenant_id, id),
  foreign key (tenant_id, course_id) references learning.course (tenant_id, id),
  check (status <> 'completed' or (completed_at is not null and completed_version is not null))
);
create unique index assignment_open on learning.assignment (tenant_id, course_id, learner_id) where status = 'open';
create index assignment_learner on learning.assignment (tenant_id, learner_id, status);
select platform.enable_tenant_rls('learning.assignment');
grant select, insert, update on learning.assignment to tb_app;

-- Lessons a learner finished, by lesson key, so progress is shared between the language versions of a course (FR-003)
-- and carries over to a new version where the lesson stays.
create table learning.lesson_progress (
  tenant_id uuid not null,
  assignment_id uuid not null,
  lesson_key text not null,
  completed_at timestamptz not null,
  primary key (tenant_id, assignment_id, lesson_key),
  foreign key (tenant_id, assignment_id) references learning.assignment (tenant_id, id)
);
select platform.enable_tenant_rls('learning.lesson_progress');
grant select, insert, delete on learning.lesson_progress to tb_app;

-- A quiz attempt with its score in percent. The answers are not kept; a question changed later leaves the score as it was.
create table learning.attempt (
  tenant_id uuid not null,
  id uuid not null,
  assignment_id uuid not null,
  lesson_key text not null,
  course_version int not null,
  score int not null check (score between 0 and 100),
  passed boolean not null,
  created_at timestamptz not null,
  primary key (tenant_id, id),
  foreign key (tenant_id, assignment_id) references learning.assignment (tenant_id, id)
);
create index attempt_assignment on learning.attempt (tenant_id, assignment_id, lesson_key);
select platform.enable_tenant_rls('learning.attempt');
grant select, insert on learning.attempt to tb_app;

-- Extra attempts a learning administrator granted (FR-019).
create table learning.attempt_grant (
  tenant_id uuid not null,
  id uuid not null,
  assignment_id uuid not null,
  lesson_key text not null,
  extra int not null check (extra between 1 and 10),
  granted_by uuid not null,
  created_at timestamptz not null,
  primary key (tenant_id, id),
  foreign key (tenant_id, assignment_id) references learning.assignment (tenant_id, id)
);
create index attempt_grant_assignment on learning.attempt_grant (tenant_id, assignment_id, lesson_key);
select platform.enable_tenant_rls('learning.attempt_grant');
grant select, insert on learning.attempt_grant to tb_app;

-- A certificate for a completion (FR-010). code is the ID printed on it, verified inside the tenant only.
create table learning.certificate (
  tenant_id uuid not null,
  id uuid not null,
  assignment_id uuid not null,
  course_id uuid not null,
  learner_id uuid not null,
  version int not null,
  code text not null,
  issued_at timestamptz not null,
  expires_at timestamptz null,
  primary key (tenant_id, id),
  foreign key (tenant_id, assignment_id) references learning.assignment (tenant_id, id)
);
create unique index certificate_code on learning.certificate (tenant_id, code);
create unique index certificate_assignment on learning.certificate (tenant_id, assignment_id);
create index certificate_learner on learning.certificate (tenant_id, learner_id);
select platform.enable_tenant_rls('learning.certificate');
grant select, insert on learning.certificate to tb_app;

-- A learning path (FR-004): ordered courses, each of which may need the one before it completed first.
create table learning.path (
  tenant_id uuid not null,
  id uuid not null,
  org_unit_id uuid null,
  status text not null default 'draft' check (status in ('draft', 'published', 'retired')),
  titles jsonb not null,
  summaries jsonb not null default '{}',
  audience jsonb not null default '{}',
  created_by uuid not null,
  created_at timestamptz not null,
  updated_at timestamptz not null,
  row_version int not null default 1,
  primary key (tenant_id, id)
);
select platform.enable_tenant_rls('learning.path');
grant select, insert, update on learning.path to tb_app;

create table learning.path_course (
  tenant_id uuid not null,
  path_id uuid not null,
  position int not null,
  course_id uuid not null,
  requires_previous boolean not null default false,
  primary key (tenant_id, path_id, position),
  foreign key (tenant_id, path_id) references learning.path (tenant_id, id),
  foreign key (tenant_id, course_id) references learning.course (tenant_id, id)
);
create unique index path_course_once on learning.path_course (tenant_id, path_id, course_id);
select platform.enable_tenant_rls('learning.path_course');
grant select, insert, delete on learning.path_course to tb_app;
