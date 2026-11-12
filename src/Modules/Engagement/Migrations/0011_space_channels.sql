-- Spec 029 FR-006: the discussion of a project or team space is a closed community bound to the space. Its members
-- are the space's members, decided by the space roles, so it stores none. classification follows the space and
-- decides whether its posts may reach AI (spec 012). One community per space.
alter table engagement.community add column space_id uuid null;
alter table engagement.community add column classification text not null default 'internal' check (classification in ('internal', 'confidential'));
create unique index community_space on engagement.community (tenant_id, space_id) where space_id is not null;
