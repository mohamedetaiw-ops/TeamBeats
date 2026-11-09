-- Spec 034 slice 4 (R1): judge calibration (US8 scenarios 1 and 4, FR-011). People with eval.review label cases for a
-- judged rubric; a calibration run asks the judge about each and measures agreement per locale. A rubric below the
-- target is demoted to warning severity in the tenant until a later calibration reaches it.
create table evaluation.calibration_label (
  tenant_id uuid not null,
  id uuid not null,
  metric text not null,
  locale text not null check (locale in ('ar', 'en')),
  input text not null,
  expectations jsonb not null,
  answer text not null,
  label text not null check (label in ('pass', 'fail')),
  labelled_by uuid not null,
  labelled_at timestamptz not null,
  primary key (tenant_id, id)
);
create index calibration_label_metric on evaluation.calibration_label (tenant_id, metric, locale);
select platform.enable_tenant_rls('evaluation.calibration_label');
grant select, insert, update, delete on evaluation.calibration_label to tb_app;

create table evaluation.calibration (
  tenant_id uuid not null,
  id uuid not null,
  metric text not null,
  rubric_version text not null,
  status text not null check (status in ('running', 'completed')),
  outcome text null check (outcome in ('calibrated', 'demoted', 'insufficient')),
  agreement jsonb not null default '{}',
  requested_by uuid not null,
  created_at timestamptz not null,
  finished_at timestamptz null,
  primary key (tenant_id, id)
);
create index calibration_metric on evaluation.calibration (tenant_id, metric, created_at desc);
create unique index calibration_one_running on evaluation.calibration (tenant_id, metric) where status = 'running';
select platform.enable_tenant_rls('evaluation.calibration');
grant select, insert, update on evaluation.calibration to tb_app;
