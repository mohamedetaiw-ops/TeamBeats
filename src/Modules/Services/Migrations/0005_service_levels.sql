-- Spec 016 R2 FR-015: a request of a service with a service level target keeps when it is due and when it becomes at
-- risk (75% of the target), set on submit, and whether it went at risk or breached, so each alert is sent once.
alter table services.request add column due_at timestamptz null;
alter table services.request add column at_risk_at timestamptz null;
alter table services.request add column service_level text null check (service_level in ('at_risk', 'breached'));
create index request_service_level on services.request (tenant_id, at_risk_at) where due_at is not null and status in ('submitted', 'in_approval', 'returned');
