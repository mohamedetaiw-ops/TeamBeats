-- Spec 032 R2 T023: a page submission waits on an approval route of the Experience module.
alter table workflow.process_definition drop constraint process_definition_source_check;
alter table workflow.process_definition add constraint process_definition_source_check
  check (source in ('service_route', 'content_route', 'policy_route', 'document_route', 'leave_route', 'journey_route', 'attendance_route', 'letter_route',
    'data_change_route', 'expense_route', 'booking_route', 'process_model', 'agent_route', 'page_route'));
