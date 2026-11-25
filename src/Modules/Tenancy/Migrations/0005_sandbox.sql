-- Spec 035 R3 (T024): a developer sandbox tenant ends on its own. It is suspended when the date passes and closed 14 days
-- later, after which the normal retention and deletion of a closed tenant apply.
alter table tenancy.tenant add column sandbox_until timestamptz null;
create index tenant_sandbox_until on tenancy.tenant (sandbox_until) where sandbox_until is not null;
