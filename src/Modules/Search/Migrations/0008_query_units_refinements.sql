-- Spec 021 T021 (FR-002, section 2 "by unit and locale", "query refinements"): each query event keeps the searcher's
-- primary org unit at search time (no person, so the log stays pseudonymous) and, when the same person searched again
-- with other words within five minutes of a search they did not click, the event it refined.
alter table search.query_event add column org_unit_id uuid null;
alter table search.query_event add column refined_from uuid null;
create index query_event_person_at on search.query_event (tenant_id, person, at);
