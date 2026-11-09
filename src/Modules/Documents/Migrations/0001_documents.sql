-- Spec 011 (R1): libraries, folders, document types, documents and their versions, saved views and download tokens.
-- Every table is tenant-owned with forced row-level security. File content lives in object storage under
-- tenants/{tenant_id}/documents/{library_id}/.
create schema documents;
grant usage on schema documents to tb_app;

-- Tenant upload rules (FR-003): the file type allowlist and the size limit. No row means the defaults in code.
create table documents.settings (
  tenant_id uuid not null primary key,
  allowed_extensions text[] not null,
  max_file_mb int not null check (max_file_mb between 1 and 2048),
  updated_at timestamptz not null default now(),
  updated_by uuid null
);
select platform.enable_tenant_rls('documents.settings');
grant select, insert, update on documents.settings to tb_app;

-- A library at tenant, org unit or site scope (FR-001). org_unit_id is the unit that owns it for authorization: the
-- unit itself, the site's unit, or null for the whole tenant.
create table documents.library (
  tenant_id uuid not null,
  id uuid not null,
  names jsonb not null,
  scope text not null check (scope in ('tenant', 'org_unit', 'site')),
  scope_id uuid null,
  org_unit_id uuid null,
  allowed_extensions text[] null,
  max_file_mb int null check (max_file_mb is null or max_file_mb between 1 and 2048),
  classification_default text not null default 'internal' check (classification_default in ('public', 'internal', 'confidential', 'restricted')),
  status text not null default 'active' check (status in ('active', 'archived')),
  created_at timestamptz not null default now(),
  created_by uuid null,
  updated_at timestamptz not null default now(),
  row_version int not null default 1,
  primary key (tenant_id, id),
  check ((scope = 'tenant') = (scope_id is null))
);
select platform.enable_tenant_rls('documents.library');
grant select, insert, update on documents.library to tb_app;

-- Folders form a tree inside a library. path holds the ancestors and the folder itself, root first. securable_id is
-- the node whose permissions apply: the folder itself after an inheritance break, otherwise what its parent uses.
create table documents.folder (
  tenant_id uuid not null,
  id uuid not null,
  library_id uuid not null,
  parent_id uuid null,
  name text not null check (length(name) between 1 and 200),
  path uuid[] not null,
  inherits boolean not null default true,
  securable_type text not null check (securable_type in ('doc_library', 'doc_folder')),
  securable_id uuid not null,
  created_at timestamptz not null default now(),
  created_by uuid null,
  primary key (tenant_id, id),
  foreign key (tenant_id, library_id) references documents.library (tenant_id, id)
);
create unique index folder_name on documents.folder (tenant_id, library_id, coalesce(parent_id, '00000000-0000-0000-0000-000000000000'::uuid), lower(name));
create index folder_path on documents.folder using gin (path);
select platform.enable_tenant_rls('documents.folder');
grant select, insert, update on documents.folder to tb_app;

-- Document types with typed metadata fields (FR-002). controlled holds the controlled document settings of US3.
create table documents.document_type (
  tenant_id uuid not null,
  id uuid not null,
  library_id uuid not null,
  names jsonb not null,
  fields jsonb not null default '[]'::jsonb,
  controlled jsonb null,
  status text not null default 'active' check (status in ('active', 'retired')),
  created_at timestamptz not null default now(),
  updated_at timestamptz not null default now(),
  row_version int not null default 1,
  primary key (tenant_id, id),
  foreign key (tenant_id, library_id) references documents.library (tenant_id, id)
);
select platform.enable_tenant_rls('documents.document_type');
grant select, insert, update on documents.document_type to tb_app;

-- A document. checked_out_by holds the exclusive editing lock. checked_in is false while the first upload waits for
-- its required fields: then only the uploader sees it (US1 scenario 1). current_version_id is the latest checked-in
-- version, effective_version_id the version readers get (FR-006).
create table documents.document (
  tenant_id uuid not null,
  id uuid not null,
  library_id uuid not null,
  folder_id uuid null,
  type_id uuid null,
  name text not null check (length(name) between 1 and 200),
  number text null,
  metadata jsonb not null default '{}'::jsonb,
  classification text not null check (classification in ('public', 'internal', 'confidential', 'restricted')),
  owner_user_id uuid not null,
  checked_in boolean not null default false,
  checked_out_by uuid null,
  checked_out_at timestamptz null,
  pending_version_id uuid null,
  current_version_id uuid null,
  effective_version_id uuid null,
  review_due_at timestamptz null,
  inherits boolean not null default true,
  securable_type text not null check (securable_type in ('doc_library', 'doc_folder', 'doc_document')),
  securable_id uuid not null,
  deleted_at timestamptz null,
  deleted_by uuid null,
  created_at timestamptz not null default now(),
  created_by uuid not null,
  updated_at timestamptz not null default now(),
  row_version int not null default 1,
  primary key (tenant_id, id),
  foreign key (tenant_id, library_id) references documents.library (tenant_id, id),
  foreign key (tenant_id, folder_id) references documents.folder (tenant_id, id),
  foreign key (tenant_id, type_id) references documents.document_type (tenant_id, id)
);
create index document_library on documents.document (tenant_id, library_id, folder_id) where deleted_at is null;
create index document_securable on documents.document (tenant_id, securable_id) where deleted_at is null;
create index document_metadata on documents.document using gin (metadata jsonb_path_ops);
select platform.enable_tenant_rls('documents.document');
grant select, insert, update on documents.document to tb_app;

-- Versions are never changed in content (FR-004). Minor versions are drafts; a major version is a release.
create table documents.document_version (
  tenant_id uuid not null,
  id uuid not null,
  document_id uuid not null,
  library_id uuid not null,
  major int not null check (major >= 0),
  minor int not null check (minor >= 0),
  object_key text not null,
  content_hash text not null,
  size_bytes bigint not null check (size_bytes > 0),
  content_type text not null,
  file_name text not null,
  note text null check (note is null or length(note) <= 1000),
  author_user_id uuid not null,
  approver_user_id uuid null,
  effective_from timestamptz null,
  state text not null check (state in ('pending', 'draft', 'in_review', 'approved', 'effective', 'superseded')),
  scan_state text not null check (scan_state in ('clean', 'infected', 'failed')),
  text_extract text null,
  created_at timestamptz not null default now(),
  primary key (tenant_id, id),
  foreign key (tenant_id, document_id) references documents.document (tenant_id, id)
);
create unique index version_number on documents.document_version (tenant_id, document_id, major, minor) where state <> 'pending';
create index version_hash on documents.document_version (tenant_id, library_id, content_hash);
select platform.enable_tenant_rls('documents.document_version');
grant select, insert, update on documents.document_version to tb_app;

-- Saved views of a library (US1 scenario 5): a name per locale, filters and a sort, made by library managers.
create table documents.saved_view (
  tenant_id uuid not null,
  id uuid not null,
  library_id uuid not null,
  names jsonb not null,
  query jsonb not null,
  created_by uuid not null,
  created_at timestamptz not null default now(),
  primary key (tenant_id, id),
  foreign key (tenant_id, library_id) references documents.library (tenant_id, id)
);
select platform.enable_tenant_rls('documents.saved_view');
grant select, insert, delete on documents.saved_view to tb_app;

-- Short-lived download tokens, issued after the permission check and bound to the user and the version. Only the
-- token's hash is stored.
create table documents.download_token (
  tenant_id uuid not null,
  token_hash bytea not null,
  user_id uuid not null,
  document_id uuid not null,
  version_id uuid not null,
  expires_at timestamptz not null,
  created_at timestamptz not null default now(),
  primary key (tenant_id, token_hash)
);
select platform.enable_tenant_rls('documents.download_token');
grant select, insert, delete on documents.download_token to tb_app;
