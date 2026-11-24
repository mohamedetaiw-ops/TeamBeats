-- Spec 034 R2 T021 (US9 scenarios 2 and 3, FR-026): a person's run that is part of an online comparison records the
-- comparison, its arm and the model the arm named, so the execution record shows which arm answered.
alter table agents.run add column comparison_id uuid null;
alter table agents.run add column arm text null check (arm is null or arm in ('champion', 'challenger'));
alter table agents.run add column requested_model text null;
