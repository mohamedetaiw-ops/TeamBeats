-- Spec 011 R3: libraries whose files are encrypted with the tenant data key (spec 026 FR-007). Turning it on cannot be
-- undone; a background pass moves the files stored before onto the key and marks each version when it is done.
alter table documents.library add column customer_key boolean not null default false;
alter table documents.library add column customer_key_at timestamptz null;
alter table documents.document_version add column encrypted boolean not null default false;
create index document_version_unencrypted on documents.document_version (tenant_id, library_id) where not encrypted;
