-- Platform schema: shared helpers used by every module migration.
-- Runs as tb_owner. Roles tb_owner, tb_app and tb_operator are created by deploy/db/bootstrap.sql.

create schema if not exists platform;
grant usage on schema platform to tb_app;

-- Current tenant from transaction-local context. Missing context yields NULL, which matches no rows (fail closed).
create or replace function platform.current_tenant_id() returns uuid
language sql stable
as $$ select nullif(current_setting('app.tenant_id', true), '')::uuid $$;

grant execute on function platform.current_tenant_id() to tb_app;

-- EnableTenantRls(table): the only way tenant-owned tables get their isolation policy (r0-solution-structure section 2).
create or replace function platform.enable_tenant_rls(target regclass) returns void
language plpgsql
as $$
begin
  execute format('alter table %s enable row level security', target);
  execute format('alter table %s force row level security', target);
  execute format('drop policy if exists tenant_isolation on %s', target);
  execute format(
    'create policy tenant_isolation on %s using (tenant_id = platform.current_tenant_id()) with check (tenant_id = platform.current_tenant_id())',
    target);
end;
$$;
