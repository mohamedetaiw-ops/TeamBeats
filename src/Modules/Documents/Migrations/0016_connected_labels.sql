-- Spec 011 R2 FR-032: the Microsoft Purview sensitivity label of each connected file, and the tenant's mapping of
-- those labels to the four TeamBeats classifications, per Microsoft 365 connection (labels belong to a directory).
-- An unlabelled file takes its library's default classification; a label with no mapping counts as confidential.
alter table documents.connected_item add column label_id text null, add column label_name text null;
create index connected_item_label on documents.connected_item (tenant_id, label_id) where label_id is not null;

create table documents.sensitivity_label_map (
  tenant_id uuid not null,
  connection_id uuid not null,
  label_id text not null,
  classification text not null check (classification in ('public', 'internal', 'confidential', 'restricted')),
  updated_by uuid not null,
  updated_at timestamptz not null,
  primary key (tenant_id, connection_id, label_id)
);
select platform.enable_tenant_rls('documents.sensitivity_label_map');
grant select, insert, update, delete on documents.sensitivity_label_map to tb_app;
