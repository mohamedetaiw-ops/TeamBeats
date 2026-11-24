-- Spec 014 R2 T016 (US8): the conversation a person's Teams chat with the bot continues. A message within 12 hours of the
-- last one continues it, so the follow-up keeps its context and the whole exchange shows in the web history.
create table agents.teams_chat (
  tenant_id uuid not null,
  user_id uuid not null,
  conversation_id uuid not null,
  updated_at timestamptz not null,
  primary key (tenant_id, user_id)
);
select platform.enable_tenant_rls('agents.teams_chat');
grant select, insert, update, delete on agents.teams_chat to tb_app;
