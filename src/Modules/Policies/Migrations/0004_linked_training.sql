-- Spec 025 with spec 028 US4: a policy can link to a training course. A material version in force gives the course to
-- everyone the policy applies to; when training_first is set, they attest only after completing it.
alter table policies.policy add column course_id uuid null;
alter table policies.policy add column training_first boolean not null default false;
