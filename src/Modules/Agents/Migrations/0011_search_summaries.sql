-- Spec 012 US6 and spec 014 R2: an AI summary above search results is stored as an assistant turn, so it keeps its
-- execution record (model, prompt version, citations) and can be marked unhelpful like any answer (spec 034 production
-- evaluation reads that feedback). Its conversation is of kind 'search' and never appears in the person's assistant
-- history; it is deleted with the history and by the same retention.
alter table agents.conversation add column kind text not null default 'chat' check (kind in ('chat', 'search'));
