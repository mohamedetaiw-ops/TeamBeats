-- Spec 010 R2 US6 (FR-018, FR-019, FR-031): the tenant's skill list, the skills people declare with a proficiency, and
-- colleagues' endorsements. Skills are on until the tenant turns them off; 90 days after that they are deleted unless
-- turned on again. A free-text skill, when the tenant allows it, joins the list as suggested until an administrator
-- approves, merges or removes it.
create table people.skill_setting (
  tenant_id uuid not null primary key,
  enabled boolean not null default true,
  allow_free_text boolean not null default false,
  turned_off_at timestamptz null,
  version int not null default 1,
  updated_by uuid null,
  updated_at timestamptz not null
);
select platform.enable_tenant_rls('people.skill_setting');
grant select, insert, update on people.skill_setting to tb_app;

create table people.skill (
  tenant_id uuid not null,
  id uuid not null,
  names jsonb not null,
  status text not null check (status in ('active', 'suggested')),
  -- Normalized names for matching and finding duplicates, built by the application.
  search text not null,
  suggested_by uuid null,
  created_at timestamptz not null,
  primary key (tenant_id, id)
);
select platform.enable_tenant_rls('people.skill');
grant select, insert, update, delete on people.skill to tb_app;

create table people.person_skill (
  tenant_id uuid not null,
  user_id uuid not null,
  skill_id uuid not null,
  proficiency text not null check (proficiency in ('basic', 'good', 'expert')),
  added_at timestamptz not null,
  primary key (tenant_id, user_id, skill_id),
  foreign key (tenant_id, skill_id) references people.skill (tenant_id, id) on delete cascade
);
create index person_skill_skill on people.person_skill (tenant_id, skill_id);
select platform.enable_tenant_rls('people.person_skill');
grant select, insert, update, delete on people.person_skill to tb_app;

create table people.endorsement (
  tenant_id uuid not null,
  user_id uuid not null,
  skill_id uuid not null,
  endorser_id uuid not null check (endorser_id <> user_id),
  hidden boolean not null default false,
  created_at timestamptz not null,
  primary key (tenant_id, user_id, skill_id, endorser_id),
  foreign key (tenant_id, user_id, skill_id) references people.person_skill (tenant_id, user_id, skill_id) on delete cascade
);
select platform.enable_tenant_rls('people.endorsement');
grant select, insert, update, delete on people.endorsement to tb_app;
