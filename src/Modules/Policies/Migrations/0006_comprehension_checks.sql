-- Spec 025 US6 (R2, FR-013): comprehension checks. A version has a question bank; each question is linked to a clause
-- so a failed attempt can name the sections to reread. A campaign may require a check with a pass mark, a number of
-- attempts and how many questions each attempt draws from the bank. Attempts keep the questions drawn, the answers
-- and the score; an attempt is answered once. After the last failed attempt the member needs follow-up.
create table policies.check_question (
  tenant_id uuid not null,
  id uuid not null,
  policy_id uuid not null,
  version_id uuid not null,
  position int not null check (position between 1 and 50),
  clause_key text not null,
  prompts jsonb not null,
  options jsonb not null,
  correct text[] not null check (cardinality(correct) >= 1),
  multiple boolean not null default false,
  created_at timestamptz not null,
  primary key (tenant_id, id),
  unique (tenant_id, version_id, position),
  foreign key (tenant_id, policy_id) references policies.policy (tenant_id, id),
  foreign key (tenant_id, version_id) references policies.policy_version (tenant_id, id)
);
select platform.enable_tenant_rls('policies.check_question');
grant select, insert, delete on policies.check_question to tb_app;

alter table policies.campaign add column check_pass_percent int null check (check_pass_percent between 1 and 100);
alter table policies.campaign add column check_attempts int null check (check_attempts between 1 and 10);
alter table policies.campaign add column check_questions int null check (check_questions between 1 and 50);
alter table policies.campaign add constraint campaign_check_complete
  check ((check_pass_percent is null) = (check_attempts is null) and (check_pass_percent is null) = (check_questions is null));

-- follow_up_at: the last attempt failed; extra_attempts: attempts a compliance officer gave back after the follow-up.
alter table policies.campaign_member add column follow_up_at timestamptz null;
alter table policies.campaign_member add column extra_attempts int not null default 0 check (extra_attempts between 0 and 90);

create table policies.check_attempt (
  tenant_id uuid not null,
  id uuid not null,
  campaign_id uuid not null,
  user_id uuid not null,
  attempt int not null check (attempt between 1 and 100),
  questions uuid[] not null,
  answers jsonb null,
  correct int null,
  total int not null,
  score int null check (score between 0 and 100),
  passed boolean null,
  reread text[] not null default '{}',
  started_at timestamptz not null,
  answered_at timestamptz null,
  primary key (tenant_id, id),
  unique (tenant_id, campaign_id, user_id, attempt),
  foreign key (tenant_id, campaign_id) references policies.campaign (tenant_id, id),
  check ((answered_at is null) = (passed is null))
);
create index check_attempt_member on policies.check_attempt (tenant_id, campaign_id, user_id, attempt desc);
select platform.enable_tenant_rls('policies.check_attempt');
grant select, insert, update on policies.check_attempt to tb_app;

-- An answered attempt never changes (FR-007 keeps answers and scores with the attestation).
create function policies.check_attempt_answered_once() returns trigger language plpgsql as $$
begin
  if old.answered_at is not null then
    raise exception 'an answered attempt cannot change' using errcode = '42501';
  end if;
  return new;
end $$;
create trigger check_attempt_answered_once before update on policies.check_attempt for each row execute function policies.check_attempt_answered_once();
