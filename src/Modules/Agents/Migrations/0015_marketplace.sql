-- Spec 020 R2 (US6, FR-010): the agent marketplace. Favourites and ratings are per person per agent; ratings keep an
-- optional comment seen only by the owners, the AI Policy Administrator and its author, and are kept two years. An
-- access request goes to the agent's owners as an inbox task; a grant lets that person use the agent with no new
-- version, since published versions are immutable.
create table agents.agent_favourite (
  tenant_id uuid not null,
  user_id uuid not null,
  agent_id uuid not null,
  created_at timestamptz not null,
  primary key (tenant_id, user_id, agent_id)
);
select platform.enable_tenant_rls('agents.agent_favourite');
grant select, insert, delete on agents.agent_favourite to tb_app;

create table agents.agent_rating (
  tenant_id uuid not null,
  user_id uuid not null,
  agent_id uuid not null,
  score int not null check (score between 1 and 5),
  comment text null check (comment is null or length(comment) between 1 and 1000),
  updated_at timestamptz not null,
  primary key (tenant_id, user_id, agent_id)
);
create index agent_rating_agent on agents.agent_rating (tenant_id, agent_id);
create index agent_rating_age on agents.agent_rating (tenant_id, updated_at);
select platform.enable_tenant_rls('agents.agent_rating');
grant select, insert, update, delete on agents.agent_rating to tb_app;

create table agents.agent_access_request (
  tenant_id uuid not null,
  id uuid not null,
  agent_id uuid not null,
  user_id uuid not null,
  status text not null check (status in ('pending', 'granted', 'denied')),
  created_at timestamptz not null,
  decided_by uuid null,
  decided_at timestamptz null,
  primary key (tenant_id, id)
);
create unique index agent_access_request_pending on agents.agent_access_request (tenant_id, agent_id, user_id) where status = 'pending';
create index agent_access_request_agent on agents.agent_access_request (tenant_id, agent_id, status);
select platform.enable_tenant_rls('agents.agent_access_request');
grant select, insert, update, delete on agents.agent_access_request to tb_app;

create table agents.agent_access_grant (
  tenant_id uuid not null,
  agent_id uuid not null,
  user_id uuid not null,
  granted_by uuid not null,
  granted_at timestamptz not null,
  primary key (tenant_id, agent_id, user_id)
);
select platform.enable_tenant_rls('agents.agent_access_grant');
grant select, insert, delete on agents.agent_access_grant to tb_app;
