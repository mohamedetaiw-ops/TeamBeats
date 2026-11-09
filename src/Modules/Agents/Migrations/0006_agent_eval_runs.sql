-- Spec 034 (R1): evaluation runs are normal execution records marked 'eval' (FR-007).
alter table agents.run drop constraint run_mode_check;
alter table agents.run add constraint run_mode_check check (mode in ('run', 'test', 'eval'));
