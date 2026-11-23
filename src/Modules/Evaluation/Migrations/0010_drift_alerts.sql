-- Spec 034 R2 T019 (US7 scenario 6, FR-024): drift alerts. One open alert per agent, metric and language while its
-- rolling production score stays below the last gate result or the metric's threshold; resolved when it recovers.
-- Examples are production sample IDs, never people.
create table evaluation.drift_alert (
  tenant_id uuid not null,
  id uuid not null,
  agent_id uuid not null,
  agent_version int not null,
  metric text not null,
  locale text not null,
  baseline double precision null,
  threshold double precision null,
  value double precision not null,
  samples int not null,
  examples uuid[] not null,
  models text[] not null,
  status text not null check (status in ('open', 'resolved')),
  detected_at timestamptz not null,
  updated_at timestamptz not null,
  resolved_at timestamptz null,
  primary key (tenant_id, id)
);
create unique index drift_alert_open on evaluation.drift_alert (tenant_id, agent_id, metric, locale) where status = 'open';
create index drift_alert_agent on evaluation.drift_alert (tenant_id, agent_id, detected_at desc);
select platform.enable_tenant_rls('evaluation.drift_alert');
grant select, insert, update, delete on evaluation.drift_alert to tb_app;
