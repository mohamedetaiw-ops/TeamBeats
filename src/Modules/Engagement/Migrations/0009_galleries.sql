-- Spec 023 (R2) slice 9: photo galleries (US7, FR-017). Albums belong to a community. Photos are scanned for malware
-- and checked for content safety before anyone else sees them, stored without location metadata, and wait for a
-- moderator when the album asks for approval or the safety check did not pass. Tagged people are told and can remove
-- their tag. Every table is tenant-owned with forced row-level security.

create table engagement.album (
  tenant_id uuid not null,
  id uuid not null,
  community_id uuid not null,
  title text not null check (length(title) between 1 and 120),
  description text null check (length(description) between 1 and 1000),
  requires_approval boolean not null,
  created_by uuid not null,
  created_at timestamptz not null,
  primary key (tenant_id, id)
);
create index album_community on engagement.album (tenant_id, community_id, created_at desc);
select platform.enable_tenant_rls('engagement.album');
grant select, insert, update on engagement.album to tb_app;

-- held_reason says why a photo waits: approval (the album asks for it), safety (the check flagged it) or unchecked
-- (the check could not run).
create table engagement.album_photo (
  tenant_id uuid not null,
  id uuid not null,
  album_id uuid not null,
  uploader_id uuid not null,
  object_key text not null,
  content_type text not null check (content_type in ('image/png', 'image/jpeg')),
  bytes int not null,
  width int not null,
  height int not null,
  caption text null check (length(caption) between 1 and 300),
  status text not null check (status in ('pending', 'visible', 'removed', 'deleted')),
  held_reason text null check (held_reason in ('approval', 'safety', 'unchecked')),
  created_at timestamptz not null,
  decided_by uuid null,
  decided_at timestamptz null,
  primary key (tenant_id, id),
  foreign key (tenant_id, album_id) references engagement.album (tenant_id, id)
);
create index album_photo_album on engagement.album_photo (tenant_id, album_id, status, created_at desc);
select platform.enable_tenant_rls('engagement.album_photo');
grant select, insert, update on engagement.album_photo to tb_app;

create table engagement.photo_tag (
  tenant_id uuid not null,
  photo_id uuid not null,
  user_id uuid not null,
  tagged_by uuid not null,
  created_at timestamptz not null,
  primary key (tenant_id, photo_id, user_id),
  foreign key (tenant_id, photo_id) references engagement.album_photo (tenant_id, id)
);
create index photo_tag_user on engagement.photo_tag (tenant_id, user_id);
select platform.enable_tenant_rls('engagement.photo_tag');
grant select, insert, delete on engagement.photo_tag to tb_app;
