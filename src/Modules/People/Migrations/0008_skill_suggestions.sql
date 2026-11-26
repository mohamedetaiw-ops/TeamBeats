-- Spec 010 R3 US9 (FR-033 to FR-035): skill suggestions from what a person published. Consent is off until the person
-- turns it on; turning it off deletes their open suggestions. A dismissed suggestion is kept so it is not offered again.
-- Only the suggested skill and the references of the cited items are kept, never the text that was read.
create table people.suggestion_consent (
  tenant_id uuid not null,
  user_id uuid not null,
  enabled boolean not null,
  changed_at timestamptz not null,
  run_day date null,
  runs int not null default 0,
  primary key (tenant_id, user_id)
);
select platform.enable_tenant_rls('people.suggestion_consent');
grant select, insert, update, delete on people.suggestion_consent to tb_app;

create table people.skill_suggestion (
  tenant_id uuid not null,
  user_id uuid not null,
  skill_id uuid not null,
  state text not null check (state in ('open', 'dismissed')),
  sources jsonb not null default '[]'::jsonb,
  created_at timestamptz not null,
  primary key (tenant_id, user_id, skill_id),
  foreign key (tenant_id, skill_id) references people.skill (tenant_id, id) on delete cascade
);
select platform.enable_tenant_rls('people.skill_suggestion');
grant select, insert, update, delete on people.skill_suggestion to tb_app;
