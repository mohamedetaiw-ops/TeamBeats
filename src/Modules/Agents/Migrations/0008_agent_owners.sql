-- Spec 020 FR-002 and the edge case "agent owner leaves": a backup owner per agent, and a mark when the owner changed
-- because the previous owner left, so the new owner looks at the agent. The mark clears when owners are set again.
alter table agents.agent add column backup_owner_user_id uuid null;
alter table agents.agent add column owner_flagged_at timestamptz null;
