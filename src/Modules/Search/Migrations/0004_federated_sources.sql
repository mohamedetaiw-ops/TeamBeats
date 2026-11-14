-- Spec 012 US5 (R2, FR-016, FR-017): which federated sources the tenant searches live. A source with no row is off,
-- so nothing is sent to an external system until a search administrator turns it on. Results are never stored.
create table search.federated_source (
  tenant_id uuid not null,
  source text not null check (length(source) between 1 and 40),
  enabled boolean not null,
  updated_by uuid not null,
  updated_at timestamptz not null,
  primary key (tenant_id, source)
);
select platform.enable_tenant_rls('search.federated_source');
grant select, insert, update on search.federated_source to tb_app;
