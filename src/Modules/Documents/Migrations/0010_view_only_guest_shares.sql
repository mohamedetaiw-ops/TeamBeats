-- Spec 011 R2 US8: view-only libraries with a dynamic watermark, and folders shared read-only with guest spaces.

-- view_only: readers preview with a watermark and cannot download (FR-016). guest_sharing: library managers may share
-- folders with guest spaces (FR-017); never for a library whose default classification is Restricted.
alter table documents.library add column view_only boolean not null default false;
alter table documents.library add column guest_sharing boolean not null default false;
alter table documents.library add constraint library_guest_sharing_not_restricted check (not (guest_sharing and classification_default = 'restricted'));

-- A folder shared with one space until it expires. The space's guests read the folder and what is under it; the share
-- grants nothing else and nothing to anyone who is not a guest. Revoked or expired shares are kept for the record.
create table documents.guest_share (
  tenant_id uuid not null,
  id uuid not null,
  library_id uuid not null,
  folder_id uuid not null,
  space_id uuid not null,
  expires_at timestamptz not null,
  created_at timestamptz not null,
  created_by uuid not null,
  revoked_at timestamptz null,
  revoked_by uuid null,
  primary key (tenant_id, id),
  foreign key (tenant_id, library_id) references documents.library (tenant_id, id),
  foreign key (tenant_id, folder_id) references documents.folder (tenant_id, id)
);
create index guest_share_live on documents.guest_share (tenant_id, library_id) where revoked_at is null;
create index guest_share_space on documents.guest_share (tenant_id, space_id) where revoked_at is null;
select platform.enable_tenant_rls('documents.guest_share');
grant select, insert, update on documents.guest_share to tb_app;

-- The watermark a preview carries for a reader of a view-only library: their name, email and the time (FR-016).
alter table documents.download_token add column watermark text null;
