-- Spec 011 R2 FR-033: import from a SharePoint document library or folder through bulk import. The import keeps where
-- it reads from (connection, document library, folder), whether all versions come along, the document type and the
-- column to field mapping; it starts by listing the source, then works through the rows like a ZIP import. Each row
-- notes whether SharePoint gave the file narrower permissions than the TeamBeats library will.
alter table documents.import add column source text not null default 'zip' check (source in ('zip', 'sharepoint')),
  add column source_options jsonb null;
alter table documents.import drop constraint import_status_check;
alter table documents.import add constraint import_status_check check (status in ('listing', 'queued', 'running', 'completed'));
alter table documents.import_row add column permissions_differ boolean null, add column versions int null;
