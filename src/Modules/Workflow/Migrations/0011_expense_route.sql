-- Spec 022 slice 6: expense claims (US8 scenario 5) run a route chosen by the claim total: the line manager, an approver
-- per amount tier above it, then finance.
alter table workflow.process_definition drop constraint process_definition_source_check;
alter table workflow.process_definition add constraint process_definition_source_check
  check (source in ('service_route', 'content_route', 'policy_route', 'document_route', 'leave_route', 'journey_route', 'attendance_route', 'letter_route',
    'data_change_route', 'expense_route'));
