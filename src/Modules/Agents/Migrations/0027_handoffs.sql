-- Spec 020 R3 (US10, FR-016): a run started by a handoff points at the run that handed over, and its depth in the
-- chain (1 for the first agent, at most 3).
alter table agents.run add column parent_run_id uuid null;
alter table agents.run add column depth int not null default 1 check (depth between 1 and 3);
create index run_parent on agents.run (tenant_id, parent_run_id) where parent_run_id is not null;
