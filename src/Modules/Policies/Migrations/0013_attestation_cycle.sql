-- Spec 025 FR-008 (second half): re-attestation on a fixed cycle. A policy can ask for a new campaign for the version
-- in force every so many months, counted from its last campaign; the person who set the cycle launches it.
alter table policies.policy add column attestation_cycle_months int null check (attestation_cycle_months is null or attestation_cycle_months between 1 and 60);
alter table policies.policy add column cycle_set_by uuid null;
alter table policies.policy add constraint policy_cycle_set_by check ((attestation_cycle_months is null) = (cycle_set_by is null));
alter table policies.campaign add column cycle boolean not null default false;
