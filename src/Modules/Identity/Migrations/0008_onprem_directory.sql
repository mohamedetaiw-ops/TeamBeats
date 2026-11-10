-- Spec 003 US6 (R2 for on-premises AD): a directory connection reads either Entra ID through Microsoft Graph or AD DS
-- through the customer's sync agent (spec 017 US6). An agent read is asynchronous, so a run remembers the agent job
-- it waits for. The read position of an agent read is kept by Integrations, so the cursors here stay empty for AD DS.
alter table identity.directory_connection add column source text not null default 'entra' check (source in ('entra', 'adds'));
alter table identity.directory_sync_run add column agent_job_id uuid null;
create index directory_sync_run_agent_job on identity.directory_sync_run (tenant_id, agent_job_id) where agent_job_id is not null;
