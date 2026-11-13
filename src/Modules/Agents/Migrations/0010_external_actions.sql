-- Spec 035 FR-022 and US3 scenarios 5 and 6 (R2): a write tool an external AI client called does not act until the
-- employee confirms the exact parameters inside TeamBeats. Each call becomes a pending action bound to the parameter
-- hash, kept as sent so the hash holds, which expires 15 minutes after it was asked for. Confirming runs exactly the
-- stored parameters; the result is kept a day so that the client asking again with the same parameters gets it, and
-- nothing is done twice.
create table agents.external_action (
  tenant_id uuid not null,
  id uuid not null,
  app_id uuid not null,
  client_id text not null,
  app_name text not null,
  user_id uuid not null,
  tool text not null,
  scoped_tools text[] not null,
  parameter_hash text not null,
  parameters text not null,
  status text not null check (status in ('pending', 'confirmed', 'rejected', 'expired', 'failed')),
  reason text null,
  result text null,
  classification text null,
  sources jsonb null,
  created_at timestamptz not null,
  expires_at timestamptz not null,
  decided_at timestamptz null,
  primary key (tenant_id, id)
);
create index external_action_user on agents.external_action (tenant_id, user_id, status, created_at desc);
create index external_action_hash on agents.external_action (tenant_id, user_id, app_id, parameter_hash, created_at desc);
select platform.enable_tenant_rls('agents.external_action');
grant select, insert, update, delete on agents.external_action to tb_app;

-- The execution record of a write tool call says whether confirmation was asked or the confirmed action ran.
alter table agents.external_call drop constraint external_call_decision_check;
alter table agents.external_call add constraint external_call_decision_check
  check (decision in ('executed', 'refused', 'confirmation_requested', 'confirmed', 'cancelled'));
