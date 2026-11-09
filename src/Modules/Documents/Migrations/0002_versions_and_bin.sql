-- Spec 011 (R1) US2: the recycle bin period per tenant (FR-018, 93 days by default, 30 to 180) and the last check-out
-- reminder, so a long check-out is reminded once a day (section 4 edge cases).
alter table documents.settings add column recycle_days int not null default 93 check (recycle_days between 30 and 180);
alter table documents.document add column checkout_reminded_at timestamptz null;
create index document_deleted on documents.document (tenant_id, deleted_at) where deleted_at is not null;
create index document_checked_out on documents.document (tenant_id, checked_out_at) where checked_out_by is not null and checked_in;
-- A working copy saved while checked out, or a pending first upload, may be discarded; recycle bin purge removes rows.
grant delete on documents.document_version to tb_app;
grant delete on documents.document to tb_app;
grant delete on documents.download_token to tb_app;
