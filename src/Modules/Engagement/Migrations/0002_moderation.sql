-- Spec 023 (R2) slice 2: moderation (US8, FR-018 to FR-021). Posts and comments can be held for review (keyword
-- filter), hidden after reports from three different people, or removed by a moderator with a reason. What an author
-- deletes themselves is "deleted", so the author is never shown a moderator's reason for it. Every table is
-- tenant-owned with forced row-level security.

-- Rows removed in slice 1 were all deleted by their authors.
alter table engagement.post drop constraint post_status_check;
update engagement.post set status = 'deleted' where status = 'removed';
alter table engagement.post add constraint post_status_check check (status in ('published', 'held', 'hidden', 'removed', 'deleted'));
-- held_reason: why a post waits for a moderator (keyword: the tenant's filter matched; reports: three people reported it).
-- pending_mentions: people the author mentioned in a held post; they are told when a moderator publishes it.
alter table engagement.post
  add column held_reason text null check (held_reason in ('keyword', 'reports')),
  add column pending_mentions uuid[] null,
  add column removed_reason text null check (length(removed_reason) between 1 and 500),
  add column removed_by uuid null,
  add column removed_at timestamptz null;

alter table engagement.comment drop constraint comment_status_check;
update engagement.comment set status = 'deleted' where status = 'removed';
alter table engagement.comment add constraint comment_status_check check (status in ('visible', 'held', 'hidden', 'removed', 'deleted'));
alter table engagement.comment
  add column held_reason text null check (held_reason in ('keyword', 'reports')),
  add column pending_mentions uuid[] null,
  add column removed_reason text null check (length(removed_reason) between 1 and 500),
  add column removed_by uuid null,
  add column removed_at timestamptz null;

-- A report of a post or comment (FR-018). Open reports have no resolved_at; a person has one open report per item, so
-- three open reports are three different people. A moderator's decision resolves them all.
create table engagement.report (
  tenant_id uuid not null,
  id uuid not null,
  target_type text not null check (target_type in ('post', 'comment')),
  target_id uuid not null,
  post_id uuid not null,
  community_id uuid not null,
  reporter_id uuid not null,
  reason text not null check (reason in ('spam', 'harassment', 'inappropriate', 'misinformation', 'other')),
  note text null check (length(note) between 1 and 500),
  created_at timestamptz not null,
  resolved_at timestamptz null,
  primary key (tenant_id, id),
  foreign key (tenant_id, post_id) references engagement.post (tenant_id, id),
  foreign key (tenant_id, community_id) references engagement.community (tenant_id, id)
);
create unique index report_open_once on engagement.report (tenant_id, target_type, target_id, reporter_id) where resolved_at is null;
create index report_open_community on engagement.report (tenant_id, community_id) where resolved_at is null;
select platform.enable_tenant_rls('engagement.report');
grant select, insert, update on engagement.report to tb_app;

-- A time-bound mute in one community (FR-021): the member reads but does not post or comment until it ends.
create table engagement.mute (
  tenant_id uuid not null,
  community_id uuid not null,
  user_id uuid not null,
  until timestamptz not null,
  reason text null check (length(reason) between 1 and 500),
  created_by uuid not null,
  created_at timestamptz not null,
  primary key (tenant_id, community_id, user_id),
  foreign key (tenant_id, community_id) references engagement.community (tenant_id, id)
);
select platform.enable_tenant_rls('engagement.mute');
grant select, insert, update, delete on engagement.mute to tb_app;

-- The tenant's keyword filter (FR-019): versioned, schema-checked configuration (constitution XXIX). A post or comment
-- with one of these words is held for review.
create table engagement.keyword_filter (
  tenant_id uuid not null primary key,
  keywords jsonb not null default '[]',
  version int not null default 1,
  updated_by uuid not null,
  updated_at timestamptz not null
);
select platform.enable_tenant_rls('engagement.keyword_filter');
grant select, insert, update on engagement.keyword_filter to tb_app;
