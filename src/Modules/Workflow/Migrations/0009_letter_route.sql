-- Spec 022 slice 5: HR letters that need approval (FR-021) are a one step route of the Hr module.
alter table workflow.process_definition drop constraint process_definition_source_check;
alter table workflow.process_definition add constraint process_definition_source_check
  check (source in ('service_route', 'content_route', 'policy_route', 'document_route', 'leave_route', 'journey_route', 'attendance_route', 'letter_route'));
