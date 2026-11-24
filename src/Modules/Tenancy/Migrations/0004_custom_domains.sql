-- Spec 033 R2 T021 (US7, FR-010): custom domains. A tenant adds a host, proves it controls the host's DNS with a TXT
-- record, and sets the app title, browser description and sign-in message shown there. Only a verified host is served.
create table tenancy.tenant_domain (
  tenant_id uuid not null,
  id uuid not null,
  host text not null check (host = lower(host)),
  state text not null check (state in ('pending', 'verified', 'failed')),
  token text not null,
  app_title jsonb not null default '{}'::jsonb,
  description jsonb not null default '{}'::jsonb,
  sign_in_message jsonb not null default '{}'::jsonb,
  created_by uuid not null,
  created_at timestamptz not null,
  verified_at timestamptz,
  checked_at timestamptz,
  failure text,
  certificate_expires_at timestamptz,
  certificate_checked_at timestamptz,
  certificate_alerted_at timestamptz,
  primary key (tenant_id, id),
  unique (tenant_id, host)
);
select platform.enable_tenant_rls('tenancy.tenant_domain');
grant select, insert, update, delete on tenancy.tenant_domain to tb_app;

-- The verified hosts the installation serves, read before any tenant is known (docs/architecture/rls-exceptions.md).
-- One tenant per host: the first to verify it holds it. tb_app only reads; rows change through sync_domain_route.
create table tenancy.domain_route (
  host text primary key check (host = lower(host)),
  tenant_id uuid not null,
  domain_id uuid not null,
  routed_at timestamptz not null default now()
);
grant select on tenancy.domain_route to tb_app;

-- Brings the route of one host in line with the current tenant's own domain row: present while that row is verified,
-- gone otherwise. It reads tenant_domain under the caller's tenant context, so a tenant can only route or unroute its
-- own hosts. Returns whether the host now routes to the current tenant (false when another tenant holds it).
create function tenancy.sync_domain_route(p_host text) returns boolean
language plpgsql security definer
set search_path = pg_catalog, tenancy, platform
as $$
declare
  t uuid := platform.current_tenant_id();
  d uuid;
begin
  if t is null then
    raise exception 'tenant context required';
  end if;
  delete from tenancy.domain_route where host = lower(p_host) and tenant_id = t;
  select id into d from tenancy.tenant_domain where host = lower(p_host) and state = 'verified';
  if d is null then
    return false;
  end if;
  insert into tenancy.domain_route (host, tenant_id, domain_id) values (lower(p_host), t, d) on conflict (host) do nothing;
  return exists (select 1 from tenancy.domain_route where host = lower(p_host) and tenant_id = t);
end;
$$;
revoke all on function tenancy.sync_domain_route(text) from public;
grant execute on function tenancy.sync_domain_route(text) to tb_app;
