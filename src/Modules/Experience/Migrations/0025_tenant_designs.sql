-- Spec 036 R3 (DLS-120 to DLS-122): tenant designs. A theme editor derives a named design from one of the four: the base
-- design and typed token values on top of it. Someone else who may publish themes approves it after the contrast check;
-- only then can it be applied to a brand, which copies its values into the brand's draft.
create table experience.tenant_design (
  tenant_id uuid not null,
  id uuid not null,
  key text not null check (key ~ '^[a-z][a-z0-9_]{1,23}$'),
  base text not null check (base in ('beats', 'atlas', 'pulse', 'studio')),
  names jsonb not null,
  description text,
  tokens jsonb not null,
  state text not null check (state in ('draft', 'proposed', 'approved', 'rejected', 'retired')),
  saved_by uuid not null,
  saved_at timestamptz not null,
  proposed_by uuid,
  proposed_at timestamptz,
  reviewed_by uuid,
  reviewed_at timestamptz,
  review_note text,
  primary key (tenant_id, id),
  unique (tenant_id, key)
);
select platform.enable_tenant_rls('experience.tenant_design');
grant select, insert, update, delete on experience.tenant_design to tb_app;
