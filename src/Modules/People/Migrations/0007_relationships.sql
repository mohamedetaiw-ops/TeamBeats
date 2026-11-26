-- Spec 010 R2 US8 (FR-028, FR-029): until when the person dismissed the completeness prompt on their own profile, and
-- the assistants people administrators record for a person (at most three, never themselves).
alter table people.profile add column completeness_dismissed_until timestamptz null;

create table people.assistant (
  tenant_id uuid not null,
  user_id uuid not null,
  assistant_id uuid not null check (assistant_id <> user_id),
  set_by uuid not null,
  set_at timestamptz not null,
  primary key (tenant_id, user_id, assistant_id)
);
create index assistant_of on people.assistant (tenant_id, assistant_id);
select platform.enable_tenant_rls('people.assistant');
grant select, insert, delete on people.assistant to tb_app;
