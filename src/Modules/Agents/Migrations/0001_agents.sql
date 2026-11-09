-- Spec 014 T001 (R1): assistant conversations, turns and feedback in the Agents module (ADR-0011). Agent definitions,
-- runs and execution records of specs 020 and 027 join this schema later. Every table is tenant-owned with forced RLS;
-- a conversation is visible only to its user, which the service enforces on every query (section 7).
create schema agents;
grant usage on schema agents to tb_app;

-- Tenant settings of the assistant (section 3: the AI Policy Administrator sets retention). Retention is in days,
-- 30 by default and at most a year (section 17 default).
create table agents.assistant_settings (
  tenant_id uuid not null primary key,
  retention_days int not null check (retention_days between 1 and 365),
  updated_at timestamptz not null,
  updated_by uuid not null
);
select platform.enable_tenant_rls('agents.assistant_settings');
grant select, insert, update on agents.assistant_settings to tb_app;

-- A conversation of one user. Its classification is the highest of its turns (section 8), so the turns that follow are
-- sent to the gateway at least at that level.
create table agents.conversation (
  tenant_id uuid not null,
  id uuid not null,
  user_id uuid not null,
  title text not null check (length(title) between 1 and 120),
  classification text not null check (classification in ('public', 'internal', 'confidential', 'restricted')),
  turns int not null default 0,
  created_at timestamptz not null,
  last_turn_at timestamptz not null,
  primary key (tenant_id, id)
);
create index conversation_user on agents.conversation (tenant_id, user_id, last_turn_at desc);
create index conversation_expiry on agents.conversation (tenant_id, last_turn_at);
select platform.enable_tenant_rls('agents.conversation');
grant select, insert, update, delete on agents.conversation to tb_app;

-- One question and what came back. Citations hold the passages the answer cites, each checked against the passages
-- retrieved for the turn (FR-003). Model, prompt version and retrieval settings are kept for evaluation (FR-021).
create table agents.turn (
  tenant_id uuid not null,
  id uuid not null,
  conversation_id uuid not null,
  user_id uuid not null,
  position int not null,
  question text not null check (length(question) between 1 and 2000),
  answer text null,
  outcome text not null check (outcome in ('answered', 'no_evidence', 'policy_blocked', 'unavailable')),
  reason text null,
  language text not null check (language in ('ar', 'en')),
  agent text null,
  citations jsonb not null default '[]',
  withheld int not null default 0,
  retrieval_degraded boolean not null default false,
  classification text not null check (classification in ('public', 'internal', 'confidential', 'restricted')),
  model text null,
  provider text null,
  prompt_version text not null,
  retrieval jsonb not null,
  input_tokens int not null default 0,
  output_tokens int not null default 0,
  created_at timestamptz not null,
  primary key (tenant_id, id),
  unique (tenant_id, conversation_id, position),
  foreign key (tenant_id, conversation_id) references agents.conversation (tenant_id, id) on delete cascade
);
select platform.enable_tenant_rls('agents.turn');
grant select, insert, delete on agents.turn to tb_app;

-- Feedback on a turn (US4 scenario 2). It outlives the conversation (section 8: 25 months), so it keeps the turn's
-- ID as its execution reference and what evaluation needs, but none of the conversation text.
create table agents.feedback (
  tenant_id uuid not null,
  turn_id uuid not null,
  user_id uuid not null,
  helpful boolean not null,
  reason text null check (reason is null or reason in ('incorrect', 'not_relevant', 'incomplete', 'unclear', 'other')),
  agent text null,
  outcome text not null,
  model text null,
  prompt_version text not null,
  created_at timestamptz not null,
  primary key (tenant_id, turn_id, user_id)
);
create index feedback_age on agents.feedback (tenant_id, created_at);
select platform.enable_tenant_rls('agents.feedback');
grant select, insert, update, delete on agents.feedback to tb_app;
