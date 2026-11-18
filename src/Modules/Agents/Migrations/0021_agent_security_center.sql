-- Spec 027 R2 T017 (US6 scenarios 4 and 5, US9 scenario 3, FR-023): the Agent Security Center.

-- Instruction-like text found in untrusted content during a run, kept so the anomaly rules and run search can use it.
create table agents.injection_finding (
  tenant_id uuid not null,
  id uuid not null,
  run_id uuid not null,
  agent_id uuid not null,
  rule text not null,
  source text null,
  at timestamptz not null,
  alert_id uuid null,
  primary key (tenant_id, id),
  -- Deferred: the item of a contextual action is read before its run row is written, in the same transaction.
  foreign key (tenant_id, run_id) references agents.run (tenant_id, id) on delete cascade deferrable initially deferred
);
create index injection_finding_agent on agents.injection_finding (tenant_id, agent_id, at);
create index injection_finding_new on agents.injection_finding (tenant_id, at) where alert_id is null;
select platform.enable_tenant_rls('agents.injection_finding');
grant select, insert, update, delete on agents.injection_finding to tb_app;

-- An anomaly rule that fired for one agent; later matches inside the rule's window join the same open alert.
create table agents.security_alert (
  tenant_id uuid not null,
  id uuid not null,
  agent_id uuid not null,
  org_unit_id uuid not null,
  rule text not null check (rule in ('denied_burst', 'injection', 'cost_spike')),
  severity text not null check (severity in ('low', 'medium', 'high', 'critical')),
  status text not null check (status in ('open', 'dismissed', 'incident')),
  matches int not null default 1,
  run_ids uuid[] not null default '{}',
  details jsonb not null default '{}',
  incident_id uuid null,
  created_at timestamptz not null,
  updated_at timestamptz not null,
  decided_by uuid null,
  primary key (tenant_id, id)
);
create index security_alert_open on agents.security_alert (tenant_id, agent_id, rule, created_at) where status = 'open';
select platform.enable_tenant_rls('agents.security_alert');
grant select, insert, update on agents.security_alert to tb_app;

create table agents.security_incident (
  tenant_id uuid not null,
  id uuid not null,
  title text not null,
  severity text not null check (severity in ('low', 'medium', 'high', 'critical')),
  status text not null check (status in ('open', 'closed')),
  agent_id uuid null,
  org_unit_id uuid not null,
  run_ids uuid[] not null default '{}',
  actions jsonb not null default '[]',
  closure_note text null,
  opened_by uuid not null,
  opened_at timestamptz not null,
  closed_by uuid null,
  closed_at timestamptz null,
  version int not null default 1,
  primary key (tenant_id, id)
);
create index security_incident_status on agents.security_incident (tenant_id, status, opened_at);
select platform.enable_tenant_rls('agents.security_incident');
grant select, insert, update on agents.security_incident to tb_app;

-- High risk agents watched for 30 days after each publication (US9 scenario 3).
create table agents.agent_watch (
  tenant_id uuid not null,
  agent_id uuid not null,
  version int not null,
  reason text not null,
  since timestamptz not null,
  until timestamptz not null,
  primary key (tenant_id, agent_id)
);
select platform.enable_tenant_rls('agents.agent_watch');
grant select, insert, update, delete on agents.agent_watch to tb_app;
