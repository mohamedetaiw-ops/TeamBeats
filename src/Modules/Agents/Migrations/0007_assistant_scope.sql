-- Spec 025 T010 (FR-011): a conversation may be kept to one item, such as "Ask about this policy". The scope is set by
-- the first turn and every later turn of the conversation keeps it.
alter table agents.conversation add column scope_source text, add column scope_item_id uuid;
alter table agents.conversation add constraint conversation_scope check ((scope_source is null) = (scope_item_id is null));
