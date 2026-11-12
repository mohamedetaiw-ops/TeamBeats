-- Spec 029 FR-006: a project or team space has its own library, created by Spaces. scope_id is the space; the space
-- roles decide who reads and adds files, so the library has no owning unit and no ACL of its own. One library per space.
alter table documents.library drop constraint library_scope_check;
alter table documents.library add constraint library_scope_check check (scope in ('tenant', 'org_unit', 'site', 'space'));
create unique index library_space on documents.library (tenant_id, scope_id) where scope = 'space';
