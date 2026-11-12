-- Spec 023 (R2) slice 3: Speak Up (US2, FR-005 to FR-007, FR-025). A Speak Up channel sends every post to the review
-- queue first. Where the channel allows it, a member may post anonymously: the anonymous post is kept in its own table
-- with no column that names or points to the person, only the day it came in, and a random id. Rate limits use a keyed
-- hash rotated daily, and the old day keys are deleted, so yesterday's hashes cannot be recomputed. Every table is
-- tenant-owned with forced row-level security.

alter table engagement.channel drop constraint channel_kind_check;
alter table engagement.channel add constraint channel_kind_check check (kind in ('discussion', 'speak_up'));
alter table engagement.channel add column allow_anonymous boolean not null default false;
alter table engagement.channel add constraint channel_anonymous_speak_up check (not allow_anonymous or kind = 'speak_up');

-- A published anonymous post has no author. edited_by_moderator says a moderator changed the words before publishing.
alter table engagement.post alter column author_id drop not null;
alter table engagement.post
  add column anonymous boolean not null default false,
  add column edited_by_moderator boolean not null default false;
alter table engagement.post add constraint post_anonymous_author check ((anonymous and author_id is null) or (not anonymous and author_id is not null));
alter table engagement.post drop constraint post_held_reason_check;
alter table engagement.post add constraint post_held_reason_check check (held_reason in ('keyword', 'reports', 'speak_up'));

-- An anonymous Speak Up post waiting for, or decided by, a moderator. No user, no time of day, a random (not time
-- ordered) id. reply_token_hash lets the author read the decision once through the link shown when they posted.
create table engagement.anon_post (
  tenant_id uuid not null,
  id uuid not null,
  community_id uuid not null,
  channel_id uuid not null,
  body jsonb null,
  text text null,
  submitted_on date not null,
  status text not null default 'pending' check (status in ('pending', 'published', 'answered', 'declined')),
  reply_token_hash text null,
  answer text null check (length(answer) between 1 and 2000),
  decline_reason text null check (length(decline_reason) between 1 and 500),
  post_id uuid null,
  decided_at timestamptz null,
  primary key (tenant_id, id),
  foreign key (tenant_id, channel_id) references engagement.channel (tenant_id, id)
);
create index anon_post_queue on engagement.anon_post (tenant_id, community_id, status);
create unique index anon_post_reply on engagement.anon_post (tenant_id, reply_token_hash) where reply_token_hash is not null;
select platform.enable_tenant_rls('engagement.anon_post');
grant select, insert, update on engagement.anon_post to tb_app;

-- Today's key for the anonymous rate limit hash. Keys for earlier days are deleted when a new day starts.
create table engagement.anon_key (
  tenant_id uuid not null,
  day date not null,
  key bytea not null,
  primary key (tenant_id, day)
);
select platform.enable_tenant_rls('engagement.anon_key');
grant select, insert, delete on engagement.anon_key to tb_app;

-- How many anonymous posts a keyed hash made today (FR-006). Rows for earlier days are deleted with their keys.
create table engagement.anon_limit (
  tenant_id uuid not null,
  day date not null,
  hash text not null,
  count int not null,
  primary key (tenant_id, day, hash)
);
select platform.enable_tenant_rls('engagement.anon_limit');
grant select, insert, update, delete on engagement.anon_limit to tb_app;
