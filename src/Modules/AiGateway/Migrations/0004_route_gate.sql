-- Spec 034 slice 3b (R1): the release gate result of a proposed route change (spec 013 FR-019, spec 034 US5 scenario 4).
-- running until spec 034 decides; passed applies the change; failed_critical rejects it; failed or incomplete keeps it
-- proposed with the failures listed, for the AI Policy Administrator to apply with a reason or replace.
alter table ai.route_change add column gate_status text null check (gate_status in ('running', 'passed', 'failed', 'incomplete', 'failed_critical'));
update ai.route_change set gate_status = 'incomplete' where status = 'proposed';
