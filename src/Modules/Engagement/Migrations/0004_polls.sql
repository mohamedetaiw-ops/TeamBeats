-- Spec 023 (R2) slice 4: polls (US3 scenario 1, FR-008). A poll lives in a community and is open to everyone who can
-- read it. Each person votes once, single or multiple choice, and may change their vote until close when the poll
-- allows it. Results show after voting or only after close. In an anonymous poll the vote is counted in poll_tally,
-- which has no user column, and only the fact that a person voted is kept in poll_voted, deleted when the poll closes.
-- Every table is tenant-owned with forced row-level security.

create table engagement.poll (
  tenant_id uuid not null,
  id uuid not null,
  community_id uuid not null,
  author_id uuid not null,
  question text not null check (length(question) between 1 and 300),
  multiple boolean not null default false,
  anonymous boolean not null default false,
  allow_change boolean not null default false,
  results text not null default 'after_vote' check (results in ('after_vote', 'after_close')),
  closes_at timestamptz not null,
  closed_at timestamptz null,
  created_at timestamptz not null,
  primary key (tenant_id, id),
  foreign key (tenant_id, community_id) references engagement.community (tenant_id, id),
  -- An anonymous vote cannot be changed, because nothing links it to the person.
  check (not (anonymous and allow_change))
);
create index poll_community on engagement.poll (tenant_id, community_id, created_at desc);
select platform.enable_tenant_rls('engagement.poll');
grant select, insert, update on engagement.poll to tb_app;

-- Options keep their position; after the first vote options can be added but not changed or removed (edge case).
create table engagement.poll_option (
  tenant_id uuid not null,
  id uuid not null,
  poll_id uuid not null,
  label text not null check (length(label) between 1 and 120),
  position int not null,
  primary key (tenant_id, id),
  foreign key (tenant_id, poll_id) references engagement.poll (tenant_id, id)
);
create index poll_option_poll on engagement.poll_option (tenant_id, poll_id, position);
select platform.enable_tenant_rls('engagement.poll_option');
grant select, insert on engagement.poll_option to tb_app;

-- A named vote: who chose which option.
create table engagement.poll_vote (
  tenant_id uuid not null,
  poll_id uuid not null,
  option_id uuid not null,
  user_id uuid not null,
  voted_at timestamptz not null,
  primary key (tenant_id, poll_id, user_id, option_id),
  foreign key (tenant_id, option_id) references engagement.poll_option (tenant_id, id)
);
select platform.enable_tenant_rls('engagement.poll_vote');
grant select, insert, delete on engagement.poll_vote to tb_app;

-- Anonymous votes: counts per option, with nobody named.
create table engagement.poll_tally (
  tenant_id uuid not null,
  poll_id uuid not null,
  option_id uuid not null,
  count int not null,
  primary key (tenant_id, poll_id, option_id),
  foreign key (tenant_id, option_id) references engagement.poll_option (tenant_id, id)
);
select platform.enable_tenant_rls('engagement.poll_tally');
grant select, insert, update on engagement.poll_tally to tb_app;

-- That a person voted in an anonymous poll, kept apart from the counts and deleted at close.
create table engagement.poll_voted (
  tenant_id uuid not null,
  poll_id uuid not null,
  user_id uuid not null,
  primary key (tenant_id, poll_id, user_id)
);
select platform.enable_tenant_rls('engagement.poll_voted');
grant select, insert, delete on engagement.poll_voted to tb_app;
