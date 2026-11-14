-- Spec 009 R2 US9 scenarios 2 and 3, FR-017: the hashtags of each live item, the people a comment mentions, and what
-- people follow (sites and topics) for the "following" feed.
create table content.item_tag (
  tenant_id uuid not null,
  item_id uuid not null,
  tag text not null check (length(tag) between 2 and 50),
  primary key (tenant_id, item_id, tag)
);
create index item_tag_by_tag on content.item_tag (tenant_id, tag, item_id);
select platform.enable_tenant_rls('content.item_tag');
grant select, insert, delete on content.item_tag to tb_app;

alter table content.comment add column mentions uuid[] null;

create table content.follow (
  tenant_id uuid not null,
  user_id uuid not null,
  kind text not null check (kind in ('site', 'topic')),
  key text not null check (length(key) between 2 and 50),
  created_at timestamptz not null default now(),
  primary key (tenant_id, user_id, kind, key)
);
select platform.enable_tenant_rls('content.follow');
grant select, insert, delete on content.follow to tb_app;
