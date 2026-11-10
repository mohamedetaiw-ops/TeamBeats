-- Spec 017 (R2) T015: a directory sync job carries the read position. The job's payload holds the cursors the agent
-- starts from (null for a full read); the agent reports the cursors for the next read when it completes the job.
alter table integrations.agent_job add column users_cursor text null check (users_cursor is null or length(users_cursor) <= 500);
alter table integrations.agent_job add column groups_cursor text null check (groups_cursor is null or length(groups_cursor) <= 500);
