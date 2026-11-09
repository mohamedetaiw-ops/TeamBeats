-- Spec 034 slice 3b (R1): release gates for proposed model route changes (US5 scenario 4, spec 013 FR-019). Each
-- published agent on a changed use case runs its current version against the proposed routes; the route gate records
-- the change, what could not be tested, and the decision once every run has finished.
alter table evaluation.run drop constraint run_trigger_check;
alter table evaluation.run add constraint run_trigger_check check (trigger in ('manual', 'gate', 'route'));
alter table evaluation.run add column route_change_id uuid null;
create index run_route_change on evaluation.run (tenant_id, route_change_id) where route_change_id is not null;

create table evaluation.route_gate (
  tenant_id uuid not null,
  route_change_id uuid not null,
  use_cases text[] not null,
  status text not null check (status in ('running', 'decided')),
  result text null check (result in ('passed', 'failed', 'incomplete', 'failed_critical')),
  failures jsonb not null default '[]',
  runs int not null,
  created_at timestamptz not null,
  decided_at timestamptz null,
  primary key (tenant_id, route_change_id)
);
select platform.enable_tenant_rls('evaluation.route_gate');
grant select, insert, update on evaluation.route_gate to tb_app;
