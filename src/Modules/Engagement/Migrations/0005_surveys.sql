-- Spec 023 (R2) slice 5: surveys (US3 scenarios 2 to 5, FR-005, FR-009, FR-010). A survey targets an org unit and
-- everything below it. It is anonymous or named. An anonymous response has no user column and keeps only the group it
-- counts in (the unit directly below the survey's unit that the person belongs to); "has responded" is kept apart in
-- survey_responded and deleted when the survey closes. Results for any group below the threshold are never shown.
-- Every table is tenant-owned with forced row-level security.

-- The tenant's survey threshold (default 5, range 3 to 50), set by engagement administrators.
create table engagement.survey_setting (
  tenant_id uuid not null primary key,
  threshold int not null check (threshold between 3 and 50),
  version int not null default 1,
  updated_by uuid not null,
  updated_at timestamptz not null
);
select platform.enable_tenant_rls('engagement.survey_setting');
grant select, insert, update on engagement.survey_setting to tb_app;

create table engagement.survey (
  tenant_id uuid not null,
  id uuid not null,
  org_unit_id uuid not null,
  author_id uuid not null,
  title text not null check (length(title) between 1 and 200),
  intro text null check (length(intro) between 1 and 2000),
  anonymous boolean not null,
  threshold int not null check (threshold between 3 and 50),
  status text not null default 'draft' check (status in ('draft', 'open', 'closed')),
  closes_at timestamptz null,
  published_at timestamptz null,
  closed_at timestamptz null,
  created_at timestamptz not null,
  primary key (tenant_id, id),
  check (status = 'draft' or closes_at is not null)
);
create index survey_unit on engagement.survey (tenant_id, org_unit_id, status);
create index survey_author on engagement.survey (tenant_id, author_id, created_at desc);
select platform.enable_tenant_rls('engagement.survey');
grant select, insert, update on engagement.survey to tb_app;

-- Questions are replaced as a whole while the survey is a draft and fixed once it is published.
create table engagement.survey_question (
  tenant_id uuid not null,
  id uuid not null,
  survey_id uuid not null,
  position int not null,
  kind text not null check (kind in ('single', 'multiple', 'scale', 'text')),
  prompt text not null check (length(prompt) between 1 and 300),
  required boolean not null default false,
  primary key (tenant_id, id),
  foreign key (tenant_id, survey_id) references engagement.survey (tenant_id, id)
);
create index survey_question_survey on engagement.survey_question (tenant_id, survey_id, position);
select platform.enable_tenant_rls('engagement.survey_question');
grant select, insert, delete on engagement.survey_question to tb_app;

create table engagement.survey_option (
  tenant_id uuid not null,
  id uuid not null,
  question_id uuid not null,
  position int not null,
  label text not null check (length(label) between 1 and 120),
  primary key (tenant_id, id),
  foreign key (tenant_id, question_id) references engagement.survey_question (tenant_id, id)
);
create index survey_option_question on engagement.survey_option (tenant_id, question_id, position);
select platform.enable_tenant_rls('engagement.survey_option');
grant select, insert, delete on engagement.survey_option to tb_app;

-- A response. user_id and submitted_at are set only in a named survey; an anonymous response has a random id, no
-- person and no time, only the group it counts in.
create table engagement.survey_response (
  tenant_id uuid not null,
  id uuid not null,
  survey_id uuid not null,
  group_unit_id uuid null,
  user_id uuid null,
  submitted_at timestamptz null,
  primary key (tenant_id, id),
  foreign key (tenant_id, survey_id) references engagement.survey (tenant_id, id),
  check ((user_id is null) = (submitted_at is null))
);
create index survey_response_survey on engagement.survey_response (tenant_id, survey_id, group_unit_id);
create unique index survey_response_named on engagement.survey_response (tenant_id, survey_id, user_id) where user_id is not null;
select platform.enable_tenant_rls('engagement.survey_response');
grant select, insert on engagement.survey_response to tb_app;

-- One row per chosen option, scale value or text answer.
create table engagement.survey_answer (
  tenant_id uuid not null,
  id uuid not null,
  response_id uuid not null,
  question_id uuid not null,
  option_id uuid null,
  value int null check (value between 1 and 5),
  text text null check (length(text) between 1 and 2000),
  primary key (tenant_id, id),
  foreign key (tenant_id, response_id) references engagement.survey_response (tenant_id, id),
  foreign key (tenant_id, question_id) references engagement.survey_question (tenant_id, id),
  check (num_nonnulls(option_id, value, text) = 1)
);
create index survey_answer_response on engagement.survey_answer (tenant_id, response_id);
create index survey_answer_question on engagement.survey_answer (tenant_id, question_id);
select platform.enable_tenant_rls('engagement.survey_answer');
grant select, insert on engagement.survey_answer to tb_app;

-- That a person answered an anonymous survey, kept apart from the responses and deleted at close (FR-010).
create table engagement.survey_responded (
  tenant_id uuid not null,
  survey_id uuid not null,
  user_id uuid not null,
  primary key (tenant_id, survey_id, user_id)
);
select platform.enable_tenant_rls('engagement.survey_responded');
grant select, insert, delete on engagement.survey_responded to tb_app;
