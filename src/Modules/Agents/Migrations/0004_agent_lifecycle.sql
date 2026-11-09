-- Specs 020 T009, T011 and 027 T010 (R1): the review of a version (who rejected it and why) and the tenant agent kill
-- switch. Tenant-owned with forced RLS.
alter table agents.agent_version add column rejected_by uuid null;
alter table agents.agent_version add column rejection_reason text null;
alter table agents.agent_version add column submitted_at timestamptz null;
alter table agents.agent add column suspended_reason text null;

-- One row per tenant once the switch is used: every agent run and tool call checks it (027 US7 scenario 2).
create table agents.kill_switch (
  tenant_id uuid not null primary key,
  active boolean not null,
  reason text not null,
  changed_by uuid not null,
  changed_at timestamptz not null
);
select platform.enable_tenant_rls('agents.kill_switch');
grant select, insert, update on agents.kill_switch to tb_app;
