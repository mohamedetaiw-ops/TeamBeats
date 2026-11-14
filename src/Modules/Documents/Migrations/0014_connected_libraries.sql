-- Spec 011 R2 US9 (FR-026): a connected library shows a SharePoint document library, or one folder of it, inside
-- TeamBeats. The content stays in SharePoint; the row holds where it is (the Integrations connection, the drive and
-- the folder) and the TeamBeats settings (names, scope, audience, upload setting, default classification).
alter table documents.library
  add column source text not null default 'teambeats' check (source in ('teambeats', 'connected')),
  add column connection_id uuid null,
  add column container_id text null,
  add column container_name text null,
  add column remote_folder_id text null,
  add column upload_enabled boolean not null default false,
  add constraint library_connected_check check ((source = 'connected') = (connection_id is not null and container_id is not null));
