-- Spec 011 R2 US5 (FR-012): the brand asset portal. Collections curated by brand managers, open to every member or
-- restricted to an org unit; assets with usage notes, usage rights that expire and renditions made on upload; access
-- requests for restricted collections; a record of every download for usage reporting.

create table documents.asset_collection (
  tenant_id uuid not null,
  id uuid not null,
  names jsonb not null,
  descriptions jsonb not null default '{}',
  -- Null: every member reads it. Otherwise members of this unit and its subtree, and people given access.
  restricted_to uuid null,
  status text not null default 'active' check (status in ('active', 'retired')),
  created_at timestamptz not null,
  created_by uuid not null,
  updated_at timestamptz not null,
  row_version int not null default 1,
  primary key (tenant_id, id)
);
select platform.enable_tenant_rls('documents.asset_collection');
grant select, insert, update on documents.asset_collection to tb_app;

create table documents.asset (
  tenant_id uuid not null,
  id uuid not null,
  collection_id uuid not null,
  names jsonb not null,
  usage_notes jsonb not null default '{}',
  object_key text not null,
  content_type text not null,
  file_name text not null,
  size_bytes bigint not null,
  sha256 text not null,
  width int null,
  height int null,
  scan_state text not null,
  -- Usage rights end here; after it the asset is hidden from the portal and the page builder picker.
  rights_expire_at timestamptz null,
  expired_at timestamptz null,
  status text not null default 'active' check (status in ('active', 'retired')),
  created_at timestamptz not null,
  created_by uuid not null,
  updated_at timestamptz not null,
  row_version int not null default 1,
  primary key (tenant_id, id),
  foreign key (tenant_id, collection_id) references documents.asset_collection (tenant_id, id)
);
create index asset_collection_idx on documents.asset (tenant_id, collection_id);
create index asset_expiring on documents.asset (tenant_id, rights_expire_at) where expired_at is null and rights_expire_at is not null;
select platform.enable_tenant_rls('documents.asset');
grant select, insert, update on documents.asset to tb_app;

-- One row per rendition of an asset: png_256, png_512, png_1024, svg (the source, when it is one) and pdf.
create table documents.asset_rendition (
  tenant_id uuid not null,
  asset_id uuid not null,
  key text not null,
  object_key text null,
  content_type text not null,
  size_bytes bigint null,
  state text not null check (state in ('ready', 'failed')),
  primary key (tenant_id, asset_id, key),
  foreign key (tenant_id, asset_id) references documents.asset (tenant_id, id)
);
select platform.enable_tenant_rls('documents.asset_rendition');
grant select, insert, update, delete on documents.asset_rendition to tb_app;

create table documents.asset_access_request (
  tenant_id uuid not null,
  id uuid not null,
  collection_id uuid not null,
  user_id uuid not null,
  reason text not null,
  status text not null default 'pending' check (status in ('pending', 'approved', 'declined')),
  created_at timestamptz not null,
  decided_at timestamptz null,
  decided_by uuid null,
  primary key (tenant_id, id),
  foreign key (tenant_id, collection_id) references documents.asset_collection (tenant_id, id)
);
create unique index asset_access_request_open on documents.asset_access_request (tenant_id, collection_id, user_id) where status = 'pending';
select platform.enable_tenant_rls('documents.asset_access_request');
grant select, insert, update on documents.asset_access_request to tb_app;

create table documents.asset_download (
  tenant_id uuid not null,
  id uuid not null,
  asset_id uuid not null,
  rendition text not null,
  user_id uuid not null,
  at timestamptz not null,
  primary key (tenant_id, id),
  foreign key (tenant_id, asset_id) references documents.asset (tenant_id, id)
);
create index asset_download_at on documents.asset_download (tenant_id, at);
select platform.enable_tenant_rls('documents.asset_download');
grant select, insert on documents.asset_download to tb_app;
