-- Spec 009 (R1): sites, content types, items with immutable published versions and locale variants, acknowledgements,
-- the materialized audience of critical items, comments and reactions. Every table is tenant-owned with forced
-- row-level security.
create schema content;
grant usage on schema content to tb_app;

-- Tenant content settings: the embed allowlist (FR-013) and the fallback locale for readers without a variant.
create table content.settings (
  tenant_id uuid not null primary key,
  embed_hosts text[] not null default '{}',
  updated_at timestamptz not null default now(),
  updated_by uuid null
);
select platform.enable_tenant_rls('content.settings');
grant select, insert, update on content.settings to tb_app;

-- R1 sites are containers: names, an org unit subtree, a default audience, a time zone and a route per content type.
create table content.site (
  tenant_id uuid not null,
  id uuid not null,
  names jsonb not null,
  org_unit_id uuid null,
  time_zone text not null,
  default_audience jsonb not null default '{}'::jsonb,
  routes jsonb not null default '{}'::jsonb,
  comments_mode text not null default 'on' check (comments_mode in ('on', 'off')),
  moderation boolean not null default false,
  is_default boolean not null default false,
  status text not null default 'active' check (status in ('active', 'archived')),
  created_at timestamptz not null default now(),
  created_by uuid null,
  updated_at timestamptz not null default now(),
  row_version int not null default 1,
  primary key (tenant_id, id)
);
create unique index site_default on content.site (tenant_id) where is_default;
select platform.enable_tenant_rls('content.site');
grant select, insert, update on content.site to tb_app;

-- Tenant settings per built-in content type (spec 009 section 6: review period, owner, history and label).
create table content.content_type (
  tenant_id uuid not null,
  key text not null,
  requires_owner boolean not null,
  review_months int null check (review_months is null or review_months between 1 and 60),
  show_history_to_readers boolean not null default false,
  under_review_label boolean not null default true,
  updated_at timestamptz not null default now(),
  primary key (tenant_id, key)
);
select platform.enable_tenant_rls('content.content_type');
grant select, insert, update on content.content_type to tb_app;

create table content.content_item (
  tenant_id uuid not null,
  id uuid not null,
  site_id uuid not null,
  type text not null,
  classification text not null default 'internal',
  primary_locale text not null,
  owner_user_id uuid null,
  backup_owner_user_id uuid null,
  review_due_at timestamptz null,
  audience jsonb not null default '{}'::jsonb,
  pinned boolean not null default false,
  featured boolean not null default false,
  critical jsonb null,
  status text not null check (status in ('draft', 'in_review', 'approved', 'scheduled', 'published', 'expired', 'archived')),
  published_version int null,
  draft_version int null,
  publish_at timestamptz null,
  expires_at timestamptz null,
  published_at timestamptz null,
  expired_at timestamptz null,
  created_at timestamptz not null default now(),
  created_by uuid not null,
  updated_at timestamptz not null default now(),
  row_version int not null default 1,
  primary key (tenant_id, id),
  foreign key (tenant_id, site_id) references content.site (tenant_id, id)
);
create index content_item_due on content.content_item (tenant_id, status, publish_at) where status = 'scheduled';
create index content_item_expiry on content.content_item (tenant_id, expires_at) where status = 'published' and expires_at is not null;
create index content_item_feed on content.content_item (tenant_id, published_at desc, id) where status = 'published';
create index content_item_site on content.content_item (tenant_id, site_id, updated_at desc);
select platform.enable_tenant_rls('content.content_item');
grant select, insert, update on content.content_item to tb_app;

-- A version holds every locale variant. Once published it never changes (FR-009); edits create the next number.
create table content.content_version (
  tenant_id uuid not null,
  item_id uuid not null,
  number int not null,
  variants jsonb not null,
  change_note text null,
  material boolean not null default true,
  author_user_id uuid not null,
  approver_user_id uuid null,
  created_at timestamptz not null default now(),
  updated_at timestamptz not null default now(),
  published_at timestamptz null,
  primary key (tenant_id, item_id, number),
  foreign key (tenant_id, item_id) references content.content_item (tenant_id, id)
);
select platform.enable_tenant_rls('content.content_version');
grant select, insert, update on content.content_version to tb_app;

create function content.version_is_immutable() returns trigger language plpgsql as $$
begin
  if old.published_at is not null and (new.variants is distinct from old.variants or new.number <> old.number
      or new.author_user_id <> old.author_user_id or new.change_note is distinct from old.change_note) then
    raise exception 'content version % of item % is published and cannot change', old.number, old.item_id;
  end if;
  return new;
end $$;
create trigger content_version_immutable before update on content.content_version
  for each row execute function content.version_is_immutable();

-- FR-007: insert only. The application role has no update or delete.
create table content.acknowledgement (
  tenant_id uuid not null,
  id uuid not null,
  item_id uuid not null,
  version_number int not null,
  user_id uuid not null,
  channel text not null,
  text_hash text not null,
  at timestamptz not null,
  primary key (tenant_id, id),
  foreign key (tenant_id, item_id) references content.content_item (tenant_id, id)
);
create unique index acknowledgement_once on content.acknowledgement (tenant_id, item_id, version_number, user_id);
select platform.enable_tenant_rls('content.acknowledgement');
grant select, insert on content.acknowledgement to tb_app;

-- Who a critical item targets now, for counting and the page-load check (section 15).
create table content.audience_member (
  tenant_id uuid not null,
  item_id uuid not null,
  user_id uuid not null,
  remind_count int not null default 0,
  remind_until timestamptz null,
  primary key (tenant_id, item_id, user_id)
);
create index audience_member_user on content.audience_member (tenant_id, user_id);
select platform.enable_tenant_rls('content.audience_member');
grant select, insert, update, delete on content.audience_member to tb_app;

create table content.comment (
  tenant_id uuid not null,
  id uuid not null,
  item_id uuid not null,
  author_user_id uuid not null,
  body text not null,
  status text not null check (status in ('visible', 'held', 'hidden', 'deleted')),
  report_count int not null default 0,
  created_at timestamptz not null default now(),
  updated_at timestamptz not null default now(),
  primary key (tenant_id, id),
  foreign key (tenant_id, item_id) references content.content_item (tenant_id, id)
);
create index comment_item on content.comment (tenant_id, item_id, created_at);
select platform.enable_tenant_rls('content.comment');
grant select, insert, update on content.comment to tb_app;

create table content.comment_report (
  tenant_id uuid not null,
  comment_id uuid not null,
  user_id uuid not null,
  at timestamptz not null default now(),
  primary key (tenant_id, comment_id, user_id)
);
select platform.enable_tenant_rls('content.comment_report');
grant select, insert on content.comment_report to tb_app;

create table content.reaction (
  tenant_id uuid not null,
  item_id uuid not null,
  user_id uuid not null,
  kind text not null,
  at timestamptz not null default now(),
  primary key (tenant_id, item_id, user_id),
  foreign key (tenant_id, item_id) references content.content_item (tenant_id, id)
);
select platform.enable_tenant_rls('content.reaction');
grant select, insert, update, delete on content.reaction to tb_app;
