-- Spec 033 R2 T020 (US6, FR-027): the starter wizard for a new organization. It creates a brand, pages from templates,
-- navigation entries and the delegation of the organization's administrator, all as drafts; this table remembers what one
-- run created and holds the organization menu until the starter is published.
create table experience.starter (
  tenant_id uuid not null,
  id uuid not null,
  org_unit_id uuid not null,
  admin_user_id uuid,
  language text not null,
  design text not null,
  navigation text not null,
  brand_id uuid not null,
  page_ids uuid[] not null,
  node_ids uuid[] not null,
  state text not null check (state in ('draft', 'published')),
  created_by uuid not null,
  created_at timestamptz not null,
  published_by uuid,
  published_at timestamptz,
  primary key (tenant_id, id),
  unique (tenant_id, org_unit_id)
);
select platform.enable_tenant_rls('experience.starter');
grant select, insert, update on experience.starter to tb_app;
