-- Spec 014 R2 US5 (AST-015): a file the person brings to a conversation. Only its extracted text is kept, never the
-- file itself; it is used only in its conversation and goes with it (cascade), by the person or by retention. A file
-- uploaded before the conversation's first turn waits with no conversation until that turn names it, and is removed
-- by the retention job if no turn ever does.
create table agents.conversation_file (
  tenant_id uuid not null,
  id uuid not null,
  conversation_id uuid null,
  user_id uuid not null,
  name text not null check (length(name) between 1 and 255),
  media_type text not null check (length(media_type) between 1 and 120),
  size_bytes bigint not null check (size_bytes > 0),
  classification text not null check (classification in ('public', 'internal', 'confidential', 'restricted')),
  text text not null,
  created_at timestamptz not null,
  primary key (tenant_id, id),
  foreign key (tenant_id, conversation_id) references agents.conversation (tenant_id, id) on delete cascade
);
create index conversation_file_conversation on agents.conversation_file (tenant_id, conversation_id);
create index conversation_file_pending on agents.conversation_file (tenant_id, user_id, created_at) where conversation_id is null;
select platform.enable_tenant_rls('agents.conversation_file');
grant select, insert, update, delete on agents.conversation_file to tb_app;
