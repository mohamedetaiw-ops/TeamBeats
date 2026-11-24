-- Spec 034 R2 T023 (US10, FR-020): the organization level. A unit may set stricter thresholds for itself and the units
-- below, and name business datasets of its own that every agent of the unit and below is tested on, as an inherited
-- suite. Business datasets are datasets of kind 'org', owned by the unit, with no agent.
alter table evaluation.dataset drop constraint dataset_kind_check;
alter table evaluation.dataset add constraint dataset_kind_check check (kind in ('agent', 'search', 'org'));
create index dataset_org on evaluation.dataset (tenant_id, org_unit_id) where kind = 'org';

create table evaluation.unit_settings (
  tenant_id uuid not null,
  org_unit_id uuid not null,
  version int not null,
  metrics jsonb not null,
  datasets uuid[] not null,
  updated_by uuid not null,
  updated_at timestamptz not null,
  primary key (tenant_id, org_unit_id)
);
select platform.enable_tenant_rls('evaluation.unit_settings');
grant select, insert, update on evaluation.unit_settings to tb_app;
