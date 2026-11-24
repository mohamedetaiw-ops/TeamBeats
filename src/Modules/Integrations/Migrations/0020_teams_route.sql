-- Spec 014 US8 (R2): a message to the TeamBeats bot names only the person's Microsoft 365 directory, so the bot finds
-- the tenants that reach Teams through that directory before any tenant context exists (docs/architecture/rls-exceptions.md).
-- tb_app only reads; rows follow each tenant's own connections through sync_teams_route, run by triggers under the
-- writer's tenant context, so a tenant can only route its own directories.
create table integrations.teams_route (
  external_tenant_id text not null,
  tenant_id uuid not null,
  primary key (external_tenant_id, tenant_id)
);
grant select on integrations.teams_route to tb_app;

create function integrations.sync_teams_route() returns trigger
language plpgsql security definer
set search_path = pg_catalog, integrations, platform
as $$
declare
  t uuid := platform.current_tenant_id();
begin
  if t is null then
    return null;
  end if;
  delete from integrations.teams_route where tenant_id = t;
  insert into integrations.teams_route (external_tenant_id, tenant_id)
  select distinct lower(c.external_tenant_id), t
  from integrations.connection c
  join integrations.connection_feature f on f.tenant_id = c.tenant_id and f.connection_id = c.id
  where c.tenant_id = t and c.kind = 'microsoft365' and c.enabled and f.feature = 'teams' and f.enabled and f.consent_status = 'granted'
  on conflict do nothing;
  return null;
end;
$$;
revoke all on function integrations.sync_teams_route() from public;

create trigger connection_teams_route after insert or update or delete on integrations.connection
  for each statement execute function integrations.sync_teams_route();
create trigger connection_feature_teams_route after insert or update or delete on integrations.connection_feature
  for each statement execute function integrations.sync_teams_route();

-- The routes of connections that exist already.
alter table integrations.connection no force row level security;
alter table integrations.connection_feature no force row level security;
insert into integrations.teams_route (external_tenant_id, tenant_id)
select distinct lower(c.external_tenant_id), c.tenant_id
from integrations.connection c
join integrations.connection_feature f on f.tenant_id = c.tenant_id and f.connection_id = c.id
where c.kind = 'microsoft365' and c.enabled and f.feature = 'teams' and f.enabled and f.consent_status = 'granted'
on conflict do nothing;
alter table integrations.connection force row level security;
alter table integrations.connection_feature force row level security;
