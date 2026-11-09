-- Spec 011 (R1) FR-009: previews in the browser. Office files are converted to PDF once per version and the PDF is kept
-- next to the file; a failed conversion is remembered so readers get "preview not available" at once. Preview tokens
-- share the download token table but open only the preview, are not counted as downloads and never serve the original
-- of a format that needs conversion.
alter table documents.document_version add column preview_key text null;
alter table documents.document_version add column preview_state text not null default 'none' check (preview_state in ('none', 'ready', 'failed'));
alter table documents.download_token add column purpose text not null default 'download' check (purpose in ('download', 'preview'));
