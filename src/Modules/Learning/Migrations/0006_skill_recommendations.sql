-- Spec 028 R3 (US9, FR-024, FR-025): courses tagged with skills from the spec 010 skill list, and the recommendations
-- a learner dismissed. Skill IDs come from the People skills contract; nothing here reads People tables.
alter table learning.course add column skills uuid[] not null default '{}' check (cardinality(skills) <= 10);
create index course_skills on learning.course using gin (skills);

create table learning.recommendation_dismissal (
  tenant_id uuid not null,
  learner_id uuid not null,
  course_id uuid not null,
  dismissed_at timestamptz not null,
  primary key (tenant_id, learner_id, course_id),
  foreign key (tenant_id, course_id) references learning.course (tenant_id, id)
);
select platform.enable_tenant_rls('learning.recommendation_dismissal');
grant select, insert on learning.recommendation_dismissal to tb_app;
