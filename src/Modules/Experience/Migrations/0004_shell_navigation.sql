-- Spec 008 (R1): the shell configuration, menus, the information architecture and slug redirects. The shell and menus
-- publish independently of pages and of each other (FR-001, FR-011); IA slugs are managed apart from templates and keep
-- a redirect from every old slug (FR-013).
create table experience.shell_config (
  tenant_id uuid not null,
  version int not null,
  definition jsonb not null,
  published_by uuid null,
  published_at timestamptz not null default now(),
  primary key (tenant_id, version)
);
select platform.enable_tenant_rls('experience.shell_config');
grant select, insert on experience.shell_config to tb_app;

-- One row per menu; scope is 'tenant', an org unit ID or an IA node ID depending on the kind.
create table experience.nav_menu (
  tenant_id uuid not null,
  menu_id uuid not null,
  kind text not null check (kind in ('global', 'organization', 'section')),
  scope text not null check (length(scope) between 1 and 64),
  version int not null,
  items jsonb not null,
  published_by uuid null,
  published_at timestamptz not null default now(),
  primary key (tenant_id, menu_id),
  unique (tenant_id, kind, scope)
);
select platform.enable_tenant_rls('experience.nav_menu');
grant select, insert, update on experience.nav_menu to tb_app;

create table experience.ia_node (
  tenant_id uuid not null,
  node_id uuid not null,
  parent_id uuid null,
  labels jsonb not null,
  slug text not null check (slug ~ '^[a-z0-9]+(-[a-z0-9]+)*(/[a-z0-9]+(-[a-z0-9]+)*)*$' and length(slug) <= 200),
  page_id uuid null,
  url text null check (url is null or length(url) <= 500),
  owner_org_unit_id uuid null,
  position int not null default 0,
  updated_at timestamptz not null default now(),
  primary key (tenant_id, node_id),
  unique (tenant_id, slug),
  foreign key (tenant_id, parent_id) references experience.ia_node (tenant_id, node_id),
  foreign key (tenant_id, page_id) references experience.page (tenant_id, id),
  check ((page_id is null) <> (url is null))
);
select platform.enable_tenant_rls('experience.ia_node');
grant select, insert, update on experience.ia_node to tb_app;

create table experience.slug_redirect (
  tenant_id uuid not null,
  old_slug text not null,
  node_id uuid not null,
  created_at timestamptz not null default now(),
  primary key (tenant_id, old_slug),
  foreign key (tenant_id, node_id) references experience.ia_node (tenant_id, node_id)
);
select platform.enable_tenant_rls('experience.slug_redirect');
grant select, insert, delete on experience.slug_redirect to tb_app;
