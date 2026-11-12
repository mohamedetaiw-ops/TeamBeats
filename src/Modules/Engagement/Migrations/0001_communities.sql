-- Spec 023 (R2) slice 1: communities, their members and channels, posts with comments one reply level deep,
-- reactions, mentions, hashtags and follows. Every table is tenant-owned with forced row-level security.
create schema engagement;
grant usage on schema engagement to tb_app;

-- A community (FR-001). kind: open (anyone joins), approval (the owners accept requests) or closed (members only, and
-- invisible to everyone else). org_unit_id links a department community: membership follows the unit and the units
-- below it (FR-002), and moderators with engagement.moderate in that scope may act. Archived communities stay readable
-- by their members and take no new posts.
create table engagement.community (
  tenant_id uuid not null,
  id uuid not null,
  names jsonb not null,
  descriptions jsonb not null default '{}',
  kind text not null check (kind in ('open', 'approval', 'closed')),
  org_unit_id uuid null,
  status text not null default 'active' check (status in ('active', 'archived')),
  created_by uuid not null,
  created_at timestamptz not null,
  updated_at timestamptz not null,
  row_version int not null default 1,
  primary key (tenant_id, id)
);
create unique index community_department on engagement.community (tenant_id, org_unit_id) where org_unit_id is not null;
select platform.enable_tenant_rls('engagement.community');
grant select, insert, update on engagement.community to tb_app;

-- Members stored by the community: people who joined, asked to join, or were added; and the owners and moderators,
-- also of department communities, whose other members come from the org unit and are not stored here.
create table engagement.community_member (
  tenant_id uuid not null,
  community_id uuid not null,
  user_id uuid not null,
  role text not null default 'member' check (role in ('member', 'moderator', 'owner')),
  status text not null default 'active' check (status in ('active', 'pending')),
  created_at timestamptz not null,
  primary key (tenant_id, community_id, user_id),
  foreign key (tenant_id, community_id) references engagement.community (tenant_id, id)
);
create index community_member_user on engagement.community_member (tenant_id, user_id);
select platform.enable_tenant_rls('engagement.community_member');
grant select, insert, update, delete on engagement.community_member to tb_app;

-- Channels inside a community. Every community gets a "general" channel when it is created; its names are empty and
-- the page shows the general channel's name from its own wording.
create table engagement.channel (
  tenant_id uuid not null,
  id uuid not null,
  community_id uuid not null,
  key text not null check (key ~ '^[a-z0-9_]{1,40}$'),
  names jsonb not null,
  kind text not null default 'discussion' check (kind in ('discussion')),
  position int not null default 0,
  created_at timestamptz not null,
  primary key (tenant_id, id),
  unique (tenant_id, community_id, key),
  foreign key (tenant_id, community_id) references engagement.community (tenant_id, id)
);
select platform.enable_tenant_rls('engagement.channel');
grant select, insert, update on engagement.channel to tb_app;

-- A post (FR-003). body is the spec 009 block document; text is its plain text for search, hashtags and mentions.
create table engagement.post (
  tenant_id uuid not null,
  id uuid not null,
  community_id uuid not null,
  channel_id uuid not null,
  author_id uuid not null,
  body jsonb not null,
  text text not null,
  status text not null default 'published' check (status in ('published', 'removed')),
  pinned boolean not null default false,
  published_at timestamptz not null,
  edited_at timestamptz null,
  comment_count int not null default 0,
  row_version int not null default 1,
  primary key (tenant_id, id),
  foreign key (tenant_id, channel_id) references engagement.channel (tenant_id, id)
);
create index post_feed on engagement.post (tenant_id, community_id, pinned desc, published_at desc, id desc);
create index post_channel on engagement.post (tenant_id, channel_id, pinned desc, published_at desc, id desc);
select platform.enable_tenant_rls('engagement.post');
grant select, insert, update on engagement.post to tb_app;

create table engagement.post_hashtag (
  tenant_id uuid not null,
  post_id uuid not null,
  tag text not null check (length(tag) between 1 and 60),
  primary key (tenant_id, post_id, tag),
  foreign key (tenant_id, post_id) references engagement.post (tenant_id, id)
);
create index post_hashtag_tag on engagement.post_hashtag (tenant_id, tag);
select platform.enable_tenant_rls('engagement.post_hashtag');
grant select, insert, delete on engagement.post_hashtag to tb_app;

-- People mentioned in a post or comment and told about it (FR-004).
create table engagement.mention (
  tenant_id uuid not null,
  post_id uuid not null,
  comment_id uuid null,
  user_id uuid not null,
  created_at timestamptz not null,
  foreign key (tenant_id, post_id) references engagement.post (tenant_id, id)
);
create unique index mention_once on engagement.mention (tenant_id, post_id, coalesce(comment_id, '00000000-0000-0000-0000-000000000000'::uuid), user_id);
select platform.enable_tenant_rls('engagement.mention');
grant select, insert on engagement.mention to tb_app;

-- Comments, with one reply level: a reply's parent is a top-level comment of the same post.
create table engagement.comment (
  tenant_id uuid not null,
  id uuid not null,
  post_id uuid not null,
  parent_id uuid null,
  author_id uuid not null,
  body text not null check (length(body) between 1 and 2000),
  status text not null default 'visible' check (status in ('visible', 'removed')),
  created_at timestamptz not null,
  edited_at timestamptz null,
  primary key (tenant_id, id),
  foreign key (tenant_id, post_id) references engagement.post (tenant_id, id)
);
create index comment_post on engagement.comment (tenant_id, post_id, created_at, id);
select platform.enable_tenant_rls('engagement.comment');
grant select, insert, update on engagement.comment to tb_app;

-- One reaction per person on a post or a comment.
create table engagement.reaction (
  tenant_id uuid not null,
  target_type text not null check (target_type in ('post', 'comment')),
  target_id uuid not null,
  user_id uuid not null,
  kind text not null,
  created_at timestamptz not null,
  primary key (tenant_id, target_type, target_id, user_id)
);
select platform.enable_tenant_rls('engagement.reaction');
grant select, insert, update, delete on engagement.reaction to tb_app;

-- People following a post: its author, those who comment and those who chose to. notified_at is the last time they
-- were told about new comments, so a busy thread tells them at most once an hour (US1 scenario 4).
create table engagement.post_follow (
  tenant_id uuid not null,
  post_id uuid not null,
  user_id uuid not null,
  notified_at timestamptz null,
  created_at timestamptz not null,
  primary key (tenant_id, post_id, user_id),
  foreign key (tenant_id, post_id) references engagement.post (tenant_id, id)
);
select platform.enable_tenant_rls('engagement.post_follow');
grant select, insert, update, delete on engagement.post_follow to tb_app;
