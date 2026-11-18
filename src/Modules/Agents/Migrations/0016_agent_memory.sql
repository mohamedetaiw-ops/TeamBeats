-- Spec 020 R2 (US9, FR-015): persistent agent memory. Off by default: it needs the tenant policy switch, a definition
-- whose memory area is persistent, and the person's acceptance per agent. Items are short notes of that person only,
-- classified and never Restricted, deleted after the agent's retention without use, on leaver, on withdrawal and when
-- the tenant turns memory off.
create table agents.agent_memory_consent (
  tenant_id uuid not null,
  user_id uuid not null,
  agent_id uuid not null,
  accepted_at timestamptz not null,
  primary key (tenant_id, user_id, agent_id)
);
select platform.enable_tenant_rls('agents.agent_memory_consent');
grant select, insert, delete on agents.agent_memory_consent to tb_app;

create table agents.agent_memory (
  tenant_id uuid not null,
  id uuid not null,
  agent_id uuid not null,
  user_id uuid not null,
  text text not null check (length(text) between 1 and 500),
  classification text not null check (classification in ('public', 'internal', 'confidential')),
  run_id uuid null,
  created_at timestamptz not null,
  updated_at timestamptz not null,
  last_used_at timestamptz not null,
  primary key (tenant_id, id)
);
create index agent_memory_owner on agents.agent_memory (tenant_id, user_id, agent_id);
create index agent_memory_use on agents.agent_memory (tenant_id, last_used_at);
select platform.enable_tenant_rls('agents.agent_memory');
grant select, insert, update, delete on agents.agent_memory to tb_app;
