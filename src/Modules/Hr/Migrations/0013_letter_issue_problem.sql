-- Spec 019 R3 FR-022: why an approved letter is not printed yet (HR data or compensation missing, HR or the converter
-- not answering), shown to HR while the worker tries again. A plain reason key, never a value.
alter table hr.letter add column if not exists issue_problem text
  check (issue_problem in ('compensation_unavailable', 'compensation_missing', 'pdf_unavailable', 'data_missing'));
