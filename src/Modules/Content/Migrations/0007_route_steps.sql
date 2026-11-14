-- Spec 009 R2 FR-003: a multi-step route records each step passed on before the last decision.
alter table content.item_event drop constraint item_event_event_check;
alter table content.item_event add constraint item_event_event_check
  check (event in ('submitted', 'step_approved', 'approved', 'returned', 'withdrawn', 'schedule_missed'));
