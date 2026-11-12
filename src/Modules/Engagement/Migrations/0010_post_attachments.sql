-- Spec 023 (R2) slice 11: images and files in posts (US1, FR-003). An attachment is uploaded to a community first,
-- checked for malware (and, for an image, for content safety) and cleaned of metadata, then joined to a post when the
-- author saves it. Until then only its uploader can open it. Speak Up posts take no attachments, so nothing can tie an
-- anonymous post to the person who wrote it. Tenant-owned with forced row-level security.

create table engagement.post_attachment (
  tenant_id uuid not null,
  id uuid not null,
  community_id uuid not null,
  post_id uuid null,
  uploader_id uuid not null,
  kind text not null check (kind in ('image', 'file')),
  name text not null check (length(name) between 1 and 200),
  content_type text not null,
  bytes int not null,
  width int null,
  height int null,
  position int not null default 0,
  object_key text not null,
  created_at timestamptz not null,
  primary key (tenant_id, id),
  foreign key (tenant_id, community_id) references engagement.community (tenant_id, id),
  check ((kind = 'image') = (width is not null and height is not null))
);
create index post_attachment_post on engagement.post_attachment (tenant_id, post_id, position) where post_id is not null;
create index post_attachment_loose on engagement.post_attachment (tenant_id, uploader_id, created_at) where post_id is null;
select platform.enable_tenant_rls('engagement.post_attachment');
grant select, insert, update, delete on engagement.post_attachment to tb_app;
