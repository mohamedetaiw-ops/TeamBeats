-- Spec 032 US5 (R1): tenant templates, copies of a platform template with their own region locks, names, variants and
-- distribution. A template has versions; a page records the version it was made from and never changes with a later
-- one (FR-018). Distribution lists org units whose subtrees may use it; an empty list is the whole tenant (FR-020).
create table experience.template (
  tenant_id uuid not null,
  template_id text not null check (template_id ~ '^[a-z][a-z0-9-]{2,39}$'),
  based_on text not null,
  based_on_version text not null,
  distribution uuid[] not null default '{}',
  created_by uuid not null,
  created_at timestamptz not null default now(),
  primary key (tenant_id, template_id)
);
select platform.enable_tenant_rls('experience.template');
grant select, insert, update on experience.template to tb_app;

create table experience.template_version (
  tenant_id uuid not null,
  template_id text not null,
  version int not null check (version >= 1),
  state text not null check (state in ('draft', 'released', 'deprecated')),
  definition jsonb not null,
  updated_by uuid not null,
  updated_at timestamptz not null default now(),
  released_at timestamptz null,
  primary key (tenant_id, template_id, version),
  foreign key (tenant_id, template_id) references experience.template (tenant_id, template_id)
);
select platform.enable_tenant_rls('experience.template_version');
grant select, insert, update on experience.template_version to tb_app;
