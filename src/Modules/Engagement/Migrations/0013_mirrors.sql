-- Spec 023 R3 US10: one-way mirroring of a community's new posts to a Slack or Teams channel (FR-030). Engagement
-- administrators set one mirror per target and community. mirror_post keeps which post went to which mirror, so a post
-- goes once even when its event is delivered again. Every table is tenant-owned with forced row-level security.
create table engagement.mirror (
  tenant_id uuid not null,
  id uuid not null,
  community_id uuid not null,
  target text not null check (target in ('slack', 'teams')),
  channel_id text not null check (length(channel_id) between 1 and 200),
  excerpt boolean not null default false,
  created_by uuid not null,
  created_at timestamptz not null,
  last_sent_at timestamptz null,
  last_failure text null check (last_failure in ('unavailable', 'channel_unavailable', 'unreachable')),
  last_failure_at timestamptz null,
  primary key (tenant_id, id),
  unique (tenant_id, community_id, target),
  foreign key (tenant_id, community_id) references engagement.community (tenant_id, id)
);
select platform.enable_tenant_rls('engagement.mirror');
grant select, insert, update, delete on engagement.mirror to tb_app;

create table engagement.mirror_post (
  tenant_id uuid not null,
  mirror_id uuid not null,
  post_id uuid not null,
  sent_at timestamptz not null,
  primary key (tenant_id, mirror_id, post_id)
);
select platform.enable_tenant_rls('engagement.mirror_post');
grant select, insert, delete on engagement.mirror_post to tb_app;
