-- Spec 012 US8 (R2, FR-020): the judged query set scores the release gate reported for a retrieval change, per metric
-- and locale, with the index in force and with the change.
alter table search.retrieval_config add column gate_scores jsonb not null default '[]';
