-- Spec 011 R2 FR-022: bulk import. A library manager uploads a ZIP with a manifest.csv; a background job adds each row's
-- file as a document with its folder, type, metadata, original dates and author, and keeps one result per row.
create table documents.import (
  tenant_id uuid not null,
  id uuid not null,
  library_id uuid not null,
  folder_id uuid null,
  object_key text null,
  file_name text not null,
  size_bytes bigint not null,
  status text not null default 'queued' check (status in ('queued', 'running', 'completed')),
  total int not null,
  created_by uuid not null,
  created_at timestamptz not null,
  finished_at timestamptz null,
  primary key (tenant_id, id),
  foreign key (tenant_id, library_id) references documents.library (tenant_id, id)
);
create index import_library on documents.import (tenant_id, library_id, created_at desc);
select platform.enable_tenant_rls('documents.import');
grant select, insert, update on documents.import to tb_app;

create table documents.import_row (
  tenant_id uuid not null,
  import_id uuid not null,
  row_number int not null,
  path text not null,
  "values" jsonb not null,
  status text not null default 'pending' check (status in ('pending', 'imported', 'incomplete', 'skipped', 'failed', 'quarantined')),
  problem text null,
  document_id uuid null,
  author_matched boolean null,
  done_at timestamptz null,
  primary key (tenant_id, import_id, row_number),
  foreign key (tenant_id, import_id) references documents.import (tenant_id, id)
);
create index import_row_pending on documents.import_row (tenant_id, import_id, row_number) where status = 'pending';
select platform.enable_tenant_rls('documents.import_row');
grant select, insert, update on documents.import_row to tb_app;
