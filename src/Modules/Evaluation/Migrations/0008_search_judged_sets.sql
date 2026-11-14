-- Spec 012 US8 (R2, FR-020) and spec 021 US10: judged query sets for search ranking are datasets of kind 'search'.
-- They belong to the tenant, not to an agent, and are managed by search administrators; each runs as the default
-- persona of its org unit. Their cases are queries whose expected sources carry a relevance grade.
alter table evaluation.dataset alter column agent_id drop not null;
alter table evaluation.dataset add column kind text not null default 'agent' check (kind in ('agent', 'search'));
alter table evaluation.dataset add constraint dataset_kind_agent check ((kind = 'agent') = (agent_id is not null));
create index dataset_search on evaluation.dataset (tenant_id, kind) where kind = 'search';
