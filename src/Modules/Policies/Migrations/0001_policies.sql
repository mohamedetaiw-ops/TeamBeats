-- Spec 025 (R1): the policy register, versions with clauses, clause comments, attestation campaigns, their members
-- and the attestation records. Every table is tenant-owned with forced row-level security. Attestations are insert
-- only for the application role (FR-007): no update or delete grant, so a record cannot change once written.
create schema policies;
grant usage on schema policies to tb_app;

-- Categories of the register (code of conduct, information security, HR and so on), a name per locale.
create table policies.category (
  tenant_id uuid not null,
  id uuid not null,
  names jsonb not null,
  created_at timestamptz not null default now(),
  primary key (tenant_id, id)
);
select platform.enable_tenant_rls('policies.category');
grant select, insert, update on policies.category to tb_app;

-- One row per policy (FR-001). org_unit_id is the unit that owns it for authorization (null for the whole tenant);
-- audience is the spec 009 audience model and says who the policy applies to. route holds the approval steps.
-- current_version is the number of the version in force, null until the first one takes effect.
create table policies.policy (
  tenant_id uuid not null,
  id uuid not null,
  code text not null check (length(code) between 1 and 40),
  names jsonb not null,
  category_id uuid null,
  owner_user_id uuid not null,
  org_unit_id uuid null,
  audience jsonb not null default '{}',
  classification text not null default 'internal' check (classification in ('public', 'internal', 'confidential', 'restricted')),
  binding_locale text null,
  route jsonb not null default '[]',
  review_months int not null default 12 check (review_months between 1 and 60),
  related uuid[] not null default '{}',
  status text not null check (status in ('draft', 'in_review', 'approved', 'published', 'retired')),
  current_version int null,
  effective_from timestamptz null,
  next_review_at timestamptz null,
  retired_at timestamptz null,
  created_by uuid not null,
  created_at timestamptz not null,
  updated_at timestamptz not null,
  row_version int not null default 1,
  primary key (tenant_id, id),
  foreign key (tenant_id, category_id) references policies.category (tenant_id, id)
);
create unique index policy_code on policies.policy (tenant_id, lower(code));
select platform.enable_tenant_rls('policies.policy');
grant select, insert, update on policies.policy to tb_app;

-- A version of a policy: its titles and clauses in every locale (clauses keep a key so anchors stay the same across
-- versions), whether the change is material, and its approval trail. number is set on final approval. A policy has at
-- most one working version (draft or in review) and at most one approved version waiting for its date.
create table policies.policy_version (
  tenant_id uuid not null,
  id uuid not null,
  policy_id uuid not null,
  number int null,
  state text not null check (state in ('draft', 'in_review', 'approved', 'effective', 'superseded')),
  locales text[] not null,
  titles jsonb not null,
  clauses jsonb not null,
  material boolean not null default true,
  change_note text null check (change_note is null or length(change_note) <= 2000),
  editors uuid[] not null,
  approvals jsonb not null default '[]',
  instance_id uuid null,
  requested_effective_from timestamptz null,
  effective_from timestamptz null,
  submitted_by uuid null,
  submitted_at timestamptz null,
  approved_at timestamptz null,
  superseded_at timestamptz null,
  created_at timestamptz not null,
  updated_at timestamptz not null,
  primary key (tenant_id, id),
  unique (tenant_id, policy_id, number),
  foreign key (tenant_id, policy_id) references policies.policy (tenant_id, id)
);
create unique index policy_version_working on policies.policy_version (tenant_id, policy_id) where state in ('draft', 'in_review');
create unique index policy_version_scheduled on policies.policy_version (tenant_id, policy_id) where state = 'approved';
create index policy_version_due on policies.policy_version (tenant_id, effective_from) where state = 'approved';
select platform.enable_tenant_rls('policies.policy_version');
grant select, insert, update on policies.policy_version to tb_app;

-- Comments of approvers on clauses of a version under review (US1 scenario 2), shown next to the clause for the owner.
create table policies.clause_comment (
  tenant_id uuid not null,
  id uuid not null,
  version_id uuid not null,
  clause_key text not null,
  author_user_id uuid not null,
  text text not null check (length(text) between 1 and 2000),
  resolved_at timestamptz null,
  resolved_by uuid null,
  created_at timestamptz not null,
  primary key (tenant_id, id),
  foreign key (tenant_id, version_id) references policies.policy_version (tenant_id, id)
);
create index clause_comment_version on policies.clause_comment (tenant_id, version_id, created_at);
select platform.enable_tenant_rls('policies.clause_comment');
grant select, insert, update on policies.clause_comment to tb_app;

-- An attestation campaign for one version (US2): who, by when, the text people confirm, the reminder days before the
-- deadline, the days a late joiner gets, and the membership types left out.
create table policies.campaign (
  tenant_id uuid not null,
  id uuid not null,
  policy_id uuid not null,
  version int not null,
  audience jsonb not null,
  deadline timestamptz not null,
  attestation_text jsonb not null,
  reminder_days int[] not null,
  late_joiner_days int not null check (late_joiner_days between 1 and 365),
  excluded_membership_types text[] not null default '{}',
  status text not null check (status in ('open', 'closed')),
  launched_by uuid not null,
  launched_at timestamptz not null,
  closed_by uuid null,
  closed_at timestamptz null,
  overdue_processed_at timestamptz null,
  primary key (tenant_id, id),
  foreign key (tenant_id, policy_id) references policies.policy (tenant_id, id)
);
create index campaign_policy on policies.campaign (tenant_id, policy_id, launched_at desc);
create unique index campaign_open_version on policies.campaign (tenant_id, policy_id, version) where status = 'open';
select platform.enable_tenant_rls('policies.campaign');
grant select, insert, update on policies.campaign to tb_app;

-- Who a campaign asks (US2 scenarios 4 and 5): each person's own due date, status and when they left the audience.
create table policies.campaign_member (
  tenant_id uuid not null,
  campaign_id uuid not null,
  user_id uuid not null,
  joined_at timestamptz not null,
  due_at timestamptz not null,
  status text not null check (status in ('open', 'attested', 'overdue', 'left_audience')),
  attested_at timestamptz null,
  left_at timestamptz null,
  reminders_sent int[] not null default '{}',
  primary key (tenant_id, campaign_id, user_id),
  foreign key (tenant_id, campaign_id) references policies.campaign (tenant_id, id)
);
create index campaign_member_user on policies.campaign_member (tenant_id, user_id, status);
create index campaign_member_due on policies.campaign_member (tenant_id, status, due_at);
select platform.enable_tenant_rls('policies.campaign_member');
grant select, insert, update on policies.campaign_member to tb_app;

-- The attestation record (FR-007): person, policy, version, the text shown and its hash, the channel and the time.
create table policies.attestation (
  tenant_id uuid not null,
  id uuid not null,
  user_id uuid not null,
  policy_id uuid not null,
  version int not null,
  campaign_id uuid null,
  attestation_text text not null,
  locale text not null,
  text_hash text not null,
  channel text not null check (channel in ('web', 'mobile', 'teams', 'email')),
  source text not null default 'online' check (source in ('online', 'paper')),
  comprehension jsonb null,
  attested_at timestamptz not null,
  primary key (tenant_id, id),
  unique (tenant_id, user_id, policy_id, version),
  foreign key (tenant_id, policy_id) references policies.policy (tenant_id, id)
);
create index attestation_policy on policies.attestation (tenant_id, policy_id, version);
select platform.enable_tenant_rls('policies.attestation');
grant select, insert on policies.attestation to tb_app;
