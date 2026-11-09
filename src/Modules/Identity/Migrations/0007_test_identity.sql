-- Spec 034 FR-006: synthetic test identities for evaluation personas. They run cases inside the tenant with the
-- persona's permissions, and can never sign in, appear in the directory or receive notifications.
alter table identity.user_account add column test_identity boolean not null default false;
alter table identity.user_account drop constraint user_account_created_via_check;
alter table identity.user_account add constraint user_account_created_via_check check (created_via in ('jit', 'admin', 'scim', 'dev', 'directory', 'test'));
alter table identity.user_account add constraint user_account_test_identity check (test_identity = (created_via = 'test'));
