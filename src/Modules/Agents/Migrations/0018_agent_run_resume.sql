-- Spec 020 T021 (plan item 6): a run keeps its answer and the citations it used, so a person who lost the connection,
-- or whose run a worker finished after a restart, reads the result later. attempt counts takeovers: a worker that
-- resumes a run cut off by a restart raises it, and the request that lost the run sees the change and rolls back.
alter table agents.run add column answer text null;
alter table agents.run add column cited jsonb not null default '[]';
alter table agents.run add column durable boolean not null default false;
alter table agents.run add column attempt int not null default 0;
create index run_resumable on agents.run (tenant_id, updated_at) where status = 'running' and durable;
