-- Spec 023 R3 US9: discussion rooms with live chat inside a community (FR-027 to FR-029). A room follows its
-- community's read, write, mute and moderation rules. Messages follow the comment rules: held by the keyword filter,
-- hidden after reports from three people, removed by a moderator with a reason, or deleted by their author. Every
-- table is tenant-owned with forced row-level security.
create table engagement.room (
  tenant_id uuid not null,
  id uuid not null,
  community_id uuid not null,
  names jsonb not null,
  status text not null default 'active' check (status in ('active', 'archived')),
  created_by uuid not null,
  created_at timestamptz not null,
  last_message_at timestamptz null,
  primary key (tenant_id, id),
  foreign key (tenant_id, community_id) references engagement.community (tenant_id, id)
);
create index room_community on engagement.room (tenant_id, community_id);
select platform.enable_tenant_rls('engagement.room');
grant select, insert, update on engagement.room to tb_app;

create table engagement.room_message (
  tenant_id uuid not null,
  id uuid not null,
  room_id uuid not null,
  author_id uuid not null,
  body text not null check (length(body) between 1 and 2000),
  created_at timestamptz not null,
  edited_at timestamptz null,
  status text not null default 'visible' check (status in ('visible', 'held', 'hidden', 'removed', 'deleted')),
  held_reason text null check (held_reason in ('keyword', 'reports')),
  pending_mentions uuid[] null,
  removed_reason text null check (length(removed_reason) between 1 and 500),
  removed_by uuid null,
  removed_at timestamptz null,
  primary key (tenant_id, id),
  foreign key (tenant_id, room_id) references engagement.room (tenant_id, id)
);
create index room_message_feed on engagement.room_message (tenant_id, room_id, created_at, id);
select platform.enable_tenant_rls('engagement.room_message');
grant select, insert, update on engagement.room_message to tb_app;

-- Reports of a room message (FR-029), kept like post and comment reports: one open report per person and message.
create table engagement.room_report (
  tenant_id uuid not null,
  id uuid not null,
  message_id uuid not null,
  room_id uuid not null,
  community_id uuid not null,
  reporter_id uuid not null,
  reason text not null check (reason in ('spam', 'harassment', 'inappropriate', 'misinformation', 'other')),
  note text null check (length(note) between 1 and 500),
  created_at timestamptz not null,
  resolved_at timestamptz null,
  primary key (tenant_id, id),
  foreign key (tenant_id, message_id) references engagement.room_message (tenant_id, id)
);
create unique index room_report_open_once on engagement.room_report (tenant_id, message_id, reporter_id) where resolved_at is null;
create index room_report_open_community on engagement.room_report (tenant_id, community_id) where resolved_at is null;
select platform.enable_tenant_rls('engagement.room_report');
grant select, insert, update on engagement.room_report to tb_app;
