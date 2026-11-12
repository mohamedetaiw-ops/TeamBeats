-- Spec 022 FR-015 with spec 028 US4: a training step gives the joiner courses in the learning module and is done when
-- every one is completed. The courses are kept on the step as the template named them.
alter table hr.journey_step drop constraint if exists journey_step_type_check;
alter table hr.journey_step add constraint journey_step_type_check
  check (type in ('task', 'attestation', 'access_removal', 'exit_interview', 'preboarding', 'training'));
alter table hr.journey_step add column courses uuid[] not null default '{}';
