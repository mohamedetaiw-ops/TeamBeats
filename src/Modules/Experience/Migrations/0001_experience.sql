-- Spec 032 (R1): component enablement, pages, drafts and immutable releases. Layouts, templates and component manifests
-- are platform data shipped in code and have no table.
create schema experience;
grant usage on schema experience to tb_app;

-- Only tenant choices are stored. A platform component with no row is enabled.
create table experience.component_enablement (
  tenant_id uuid not null,
  component_id text not null,
  enabled boolean not null,
  changed_by uuid null,
  changed_at timestamptz not null default now(),
  primary key (tenant_id, component_id)
);
select platform.enable_tenant_rls('experience.component_enablement');
grant select, insert, update on experience.component_enablement to tb_app;

-- A page belongs to the tenant (org_unit_id null) or to one org unit, by immutable ID (spec 033 FR-026).
create table experience.page (
  tenant_id uuid not null,
  id uuid not null,
  org_unit_id uuid null,
  slug text not null check (slug ~ '^[a-z0-9]+(-[a-z0-9]+)*(/[a-z0-9]+(-[a-z0-9]+)*)*$' and length(slug) <= 200),
  owner_id uuid not null,
  template_id text not null,
  template_version text not null,
  current_release int null,
  created_at timestamptz not null default now(),
  primary key (tenant_id, id),
  unique (tenant_id, slug)
);
select platform.enable_tenant_rls('experience.page');
grant select, insert, update on experience.page to tb_app;

-- One working draft per page, replaced on save; revision is the optimistic concurrency token (FR-042).
create table experience.page_draft (
  tenant_id uuid not null,
  page_id uuid not null,
  revision int not null,
  definition jsonb not null,
  updated_by uuid not null,
  updated_at timestamptz not null default now(),
  primary key (tenant_id, page_id),
  foreign key (tenant_id, page_id) references experience.page (tenant_id, id)
);
select platform.enable_tenant_rls('experience.page_draft');
grant select, insert, update on experience.page_draft to tb_app;

-- Releases are immutable (FR-050, FR-052): the application role can insert and read, never update or delete.
create table experience.page_release (
  tenant_id uuid not null,
  page_id uuid not null,
  release int not null,
  definition jsonb not null,
  published_by uuid not null,
  published_at timestamptz not null default now(),
  reason text null,
  rollback_of int null,
  primary key (tenant_id, page_id, release),
  foreign key (tenant_id, page_id) references experience.page (tenant_id, id)
);
select platform.enable_tenant_rls('experience.page_release');
grant select, insert on experience.page_release to tb_app;
