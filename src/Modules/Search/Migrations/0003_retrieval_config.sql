-- Spec 012 US3 scenario 3 and FR-008 (R1): retrieval configuration as a versioned tenant record. A change builds a
-- shadow index with the proposed passage size, overlap and embedding model, waits for the spec 034 release gate, and
-- replaces the live index only after the gate passed or an administrator applied it with a reason. A tenant with no
-- active row uses the platform defaults (version 0).
create table search.retrieval_config (
  tenant_id uuid not null,
  id uuid not null,
  version int not null check (version >= 1),
  passage_tokens int not null check (passage_tokens between 100 and 1000),
  overlap_percent int not null check (overlap_percent between 0 and 50),
  top_k int not null check (top_k between 1 and 20),
  embedding_model text null check (embedding_model is null or length(embedding_model) between 1 and 100),
  status text not null check (status in ('building', 'gating', 'held', 'active', 'rejected', 'cancelled', 'superseded')),
  proposed_by uuid null,
  proposed_at timestamptz not null,
  built_at timestamptz null,
  shadow_units int null,
  gate_result text null check (gate_result is null or gate_result in ('passed', 'failed', 'incomplete', 'failed_critical')),
  gate_failures jsonb not null default '[]',
  decided_at timestamptz null,
  applied_by uuid null,
  override_reason text null,
  applied_at timestamptz null,
  primary key (tenant_id, id)
);
create unique index retrieval_config_version on search.retrieval_config (tenant_id, version);
-- One change at a time, and one configuration in force.
create unique index retrieval_config_open on search.retrieval_config (tenant_id) where status in ('building', 'gating', 'held');
create unique index retrieval_config_active on search.retrieval_config (tenant_id) where status = 'active';
select platform.enable_tenant_rls('search.retrieval_config');
grant select, insert, update on search.retrieval_config to tb_app;

-- The shadow index of the open change: the same shape as search.unit, kept in step by the indexer while the change is
-- open, copied into search.unit when the change applies and emptied when it ends.
create table search.shadow_unit (like search.unit including defaults including constraints);
alter table search.shadow_unit add primary key (tenant_id, id);
create index shadow_unit_item on search.shadow_unit (tenant_id, source, item_id);
create index shadow_unit_tsv on search.shadow_unit using gin (tsv);
create index shadow_unit_embedding_1536 on search.shadow_unit using hnsw ((embedding::extensions.vector(1536)) extensions.vector_cosine_ops)
  where embedding_dims = 1536;
select platform.enable_tenant_rls('search.shadow_unit');
grant select, insert, update, delete on search.shadow_unit to tb_app;
