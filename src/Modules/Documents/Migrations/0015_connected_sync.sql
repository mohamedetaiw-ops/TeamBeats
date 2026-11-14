-- Spec 011 R2 US9 (FR-027, FR-028, FR-031): the derived copy TeamBeats keeps of a connected library, for search and for
-- the last synced listing when SharePoint cannot be reached. Metadata and mirrored readers only, never content. A
-- reader is a lowercase email; anyone means the item is open to everyone in the organization.
create table documents.connected_item (
  tenant_id uuid not null,
  id uuid not null,
  library_id uuid not null,
  remote_id text not null,
  parent_remote_id text null,
  name text not null,
  folder boolean not null,
  media_type text null,
  size bigint null,
  modified_at timestamptz null,
  remote_version text null,
  web_link text null,
  readers text[] not null default '{}',
  anyone boolean not null default false,
  synced_at timestamptz not null,
  primary key (tenant_id, id),
  unique (tenant_id, library_id, remote_id),
  foreign key (tenant_id, library_id) references documents.library (tenant_id, id)
);
select platform.enable_tenant_rls('documents.connected_item');
grant select, insert, update, delete on documents.connected_item to tb_app;
create index connected_item_parent on documents.connected_item (tenant_id, library_id, parent_remote_id);

-- One row per connected library: the change cursor, when a sync was last queued, when one last succeeded, when the
-- library was last walked in full, and whether SharePoint answered (degraded since when, and why).
create table documents.connected_sync (
  tenant_id uuid not null,
  library_id uuid not null,
  cursor text null,
  last_sync_at timestamptz null,
  last_success_at timestamptz null,
  last_full_at timestamptz null,
  degraded_since timestamptz null,
  reason text null,
  primary key (tenant_id, library_id),
  foreign key (tenant_id, library_id) references documents.library (tenant_id, id)
);
select platform.enable_tenant_rls('documents.connected_sync');
grant select, insert, update, delete on documents.connected_sync to tb_app;
