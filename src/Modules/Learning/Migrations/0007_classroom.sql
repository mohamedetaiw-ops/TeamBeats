-- Spec 028 R3 (US10, FR-026 to FR-030): classroom sessions of a classroom lesson, and the learners' seats. A session
-- belongs to the course and a lesson key, so it survives new versions of the course. A learner holds at most one live
-- seat (booked or waiting) per lesson; waiting seats get a booked one in the order they were taken. reminded_at is set
-- when the day-before reminder went out. Attendance is recorded on the seat (attended or absent).
create table learning.session (
  tenant_id uuid not null,
  id uuid not null,
  course_id uuid not null,
  lesson_key text not null,
  starts_at timestamptz not null,
  ends_at timestamptz not null,
  place text null check (place is null or length(place) <= 200),
  link text null check (link is null or length(link) <= 500),
  capacity int not null check (capacity between 1 and 500),
  facilitator_id uuid not null,
  status text not null default 'scheduled' check (status in ('scheduled', 'cancelled')),
  created_by uuid not null,
  created_at timestamptz not null,
  updated_at timestamptz not null,
  primary key (tenant_id, id),
  foreign key (tenant_id, course_id) references learning.course (tenant_id, id),
  check (ends_at > starts_at and ends_at <= starts_at + interval '12 hours')
);
create index session_course on learning.session (tenant_id, course_id, lesson_key, starts_at);
create index session_facilitator on learning.session (tenant_id, facilitator_id, starts_at);
select platform.enable_tenant_rls('learning.session');
grant select, insert, update on learning.session to tb_app;

create table learning.seat (
  tenant_id uuid not null,
  id uuid not null,
  session_id uuid not null,
  course_id uuid not null,
  lesson_key text not null,
  assignment_id uuid not null,
  learner_id uuid not null,
  status text not null check (status in ('booked', 'waiting', 'cancelled', 'attended', 'absent')),
  reminded_at timestamptz null,
  recorded_by uuid null,
  created_at timestamptz not null,
  updated_at timestamptz not null,
  primary key (tenant_id, id),
  foreign key (tenant_id, session_id) references learning.session (tenant_id, id),
  foreign key (tenant_id, assignment_id) references learning.assignment (tenant_id, id)
);
create unique index seat_live on learning.seat (tenant_id, learner_id, course_id, lesson_key) where status in ('booked', 'waiting');
create index seat_session on learning.seat (tenant_id, session_id, status, created_at);
select platform.enable_tenant_rls('learning.seat');
grant select, insert, update on learning.seat to tb_app;
