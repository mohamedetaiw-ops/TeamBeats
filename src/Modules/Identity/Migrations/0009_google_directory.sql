-- Spec 017 T019 with spec 003 US6: a directory connection can also read Google Workspace through the Admin SDK, on a
-- Google Workspace connection. Its reads are complete, so its cursors hold only the time of the last read.
alter table identity.directory_connection drop constraint directory_connection_source_check;
alter table identity.directory_connection add constraint directory_connection_source_check check (source in ('entra', 'adds', 'google'));
