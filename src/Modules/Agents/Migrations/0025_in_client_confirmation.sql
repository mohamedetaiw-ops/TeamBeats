-- Spec 035 R3 (T026): where the employee decided a pending external AI client action, on the TeamBeats card or inside
-- a verified AI client that asked them (MCP elicitation). Empty for actions that expired or were decided before.
alter table agents.external_action add column decided_in text check (decided_in in ('teambeats', 'client'));
