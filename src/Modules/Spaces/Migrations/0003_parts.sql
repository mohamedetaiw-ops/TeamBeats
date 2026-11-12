-- Spec 029 FR-006 and US5: a space's library (spec 011) and discussion (spec 023), kept by those modules and named
-- here by ID, and the library files a task links to. A linked file is read through Documents with the reader's own
-- permissions, so a link never opens a file to someone who could not open it in the library.
alter table spaces.space add column library_id uuid null;
alter table spaces.space add column community_id uuid null;

create table spaces.task_file (
  tenant_id uuid not null,
  task_id uuid not null,
  document_id uuid not null,
  added_by uuid not null,
  added_at timestamptz not null,
  primary key (tenant_id, task_id, document_id),
  foreign key (tenant_id, task_id) references spaces.task (tenant_id, id) on delete cascade
);
select platform.enable_tenant_rls('spaces.task_file');
grant select, insert, delete on spaces.task_file to tb_app;
