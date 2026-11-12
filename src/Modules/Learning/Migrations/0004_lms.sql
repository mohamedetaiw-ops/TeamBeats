-- Spec 028 slice 4 (US6, FR-015): courses imported from the tenant's LMS. external_id is the course's ID in the LMS and
-- launch_url opens it there; an imported course has no lessons and is completed only by a completion the LMS reports.
-- lms_push records that an open assignment of an imported course was handed to the LMS ('queued') or refused by it.
alter table learning.course
  add column external_id text null check (external_id is null or length(external_id) between 1 and 200),
  add column launch_url text null check (launch_url is null or launch_url like 'https://%');
create unique index course_external on learning.course (tenant_id, external_id) where external_id is not null;

alter table learning.assignment
  add column lms_push text null check (lms_push is null or lms_push in ('queued', 'refused'));
create index assignment_lms_push on learning.assignment (tenant_id, course_id) where status = 'open' and lms_push is null;

-- Where the LMS sync of a tenant stands: whether the LMS answered last time, when the catalogue and completions were
-- last read, and the time completions are next read from (with an overlap, since the LMS may record them late).
create table learning.lms_state (
  tenant_id uuid not null,
  available boolean not null default true,
  catalogue_at timestamptz null,
  completions_at timestamptz null,
  completions_since timestamptz null,
  checked_at timestamptz not null,
  primary key (tenant_id)
);
select platform.enable_tenant_rls('learning.lms_state');
grant select, insert, update on learning.lms_state to tb_app;
