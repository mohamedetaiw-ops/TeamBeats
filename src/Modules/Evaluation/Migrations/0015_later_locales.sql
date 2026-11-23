-- Spec 034 R2 T024 (US1 scenario 6): cases of a run may be in French, Urdu and Hindi for tenants with those locales.
alter table evaluation.case_result drop constraint case_result_locale_check;
alter table evaluation.case_result add constraint case_result_locale_check check (locale in ('en', 'ar', 'fr', 'ur', 'hi'));
