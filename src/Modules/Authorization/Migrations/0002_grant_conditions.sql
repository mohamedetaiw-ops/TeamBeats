-- Spec 004 User Story 7 (FR-014 to FR-016): optional attribute conditions on role assignments and resource permissions.
-- The application validates conditions against the attribute catalogue before writing; null means no condition.
alter table authz.role_assignment add column condition jsonb null check (condition is null or jsonb_typeof(condition) = 'object');
alter table authz.resource_acl add column condition jsonb null check (condition is null or jsonb_typeof(condition) = 'object');
