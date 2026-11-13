-- Spec 016 R2 FR-014: a request started by a trigger keeps the event it came from, so one event starts one request
-- (idempotent per event), and its place in a chain of requests started by other requests, which stops past depth 5.
alter table services.request add column trigger_event text null;
alter table services.request add column chain_depth int not null default 0 check (chain_depth between 0 and 5);
create unique index request_trigger_event on services.request (tenant_id, service_id, trigger_event) where trigger_event is not null;
