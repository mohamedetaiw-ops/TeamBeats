-- Spec 012 (R1): the search index, a derived read model rebuilt from the owning modules at any time (FR-012). One row
-- per passage of a unit an owner gave (an article section, an FAQ pair, a policy clause, a document's text, a service,
-- a person). Permissions are decided again at query time by the owner, so the index holds only coarse facts.
-- The vector type comes from pgvector in the extensions schema, created by the database bootstrap (deploy/db/bootstrap.sql).
create schema search;
grant usage on schema search to tb_app;

create table search.unit (
  tenant_id uuid not null,
  id uuid not null,
  source text not null check (length(source) between 1 and 40),
  item_id uuid not null,
  unit_key text not null check (length(unit_key) <= 200),
  position int not null,
  kind text not null check (length(kind) between 1 and 40),
  locale text not null check (length(locale) between 2 and 10),
  title text not null,
  body text not null,
  link text not null,
  classification text not null check (classification in ('public', 'internal', 'confidential', 'restricted')),
  org_unit_id uuid null,
  version int null,
  updated_at timestamptz null,
  synced_at timestamptz not null,
  tsv tsvector not null,
  embedding extensions.vector null,
  embedding_dims int null,
  embedding_model text null,
  primary key (tenant_id, id)
);
create index unit_item on search.unit (tenant_id, source, item_id);
create index unit_tsv on search.unit using gin (tsv);
create index unit_kind on search.unit (tenant_id, kind, updated_at);
-- Approximate nearest neighbour search for the production embedding size; other sizes are scanned exactly.
create index unit_embedding_1536 on search.unit using hnsw ((embedding::extensions.vector(1536)) extensions.vector_cosine_ops)
  where embedding_dims = 1536;
select platform.enable_tenant_rls('search.unit');
grant select, insert, update, delete on search.unit to tb_app;

-- When each item was last read from its owner, also when it had no units, for the lag view (FR-006).
create table search.item_state (
  tenant_id uuid not null,
  source text not null,
  item_id uuid not null,
  changed_at timestamptz not null,
  synced_at timestamptz not null,
  units int not null,
  primary key (tenant_id, source, item_id)
);
select platform.enable_tenant_rls('search.item_state');
grant select, insert, update, delete on search.item_state to tb_app;

-- Rebuilds (FR-012): started by an administrator, a new tenant or a configuration change; run by the worker.
create table search.rebuild (
  tenant_id uuid not null,
  id uuid not null,
  requested_at timestamptz not null,
  requested_by uuid null,
  started_at timestamptz null,
  completed_at timestamptz null,
  items int not null default 0,
  units int not null default 0,
  primary key (tenant_id, id)
);
select platform.enable_tenant_rls('search.rebuild');
grant select, insert, update on search.rebuild to tb_app;
