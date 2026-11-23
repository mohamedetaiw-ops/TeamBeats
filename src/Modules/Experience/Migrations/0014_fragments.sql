-- Spec 032 R2 T027 (FR-045): governed fragments. A fragment is a reusable set of placements with an owner, a scope (an
-- org unit and the units below it, or the whole tenant) and immutable versions. Pages place it by reference
-- (`fragment:<key>`), never as copies, and show its current version.
create table experience.fragment (
  tenant_id uuid not null,
  id uuid not null,
  key text not null check (key ~ '^[a-z][a-z0-9-]{2,39}$'),
  names jsonb not null,
  org_unit_id uuid null,
  owner_id uuid not null,
  current_version int not null check (current_version >= 1),
  archived_at timestamptz null,
  created_at timestamptz not null,
  primary key (tenant_id, id),
  unique (tenant_id, key)
);
select platform.enable_tenant_rls('experience.fragment');
grant select, insert, update on experience.fragment to tb_app;

-- Versions are immutable: the application role inserts and reads them only.
create table experience.fragment_version (
  tenant_id uuid not null,
  fragment_id uuid not null,
  version int not null check (version >= 1),
  placements jsonb not null,
  note text null check (length(note) <= 500),
  created_by uuid not null,
  created_at timestamptz not null,
  primary key (tenant_id, fragment_id, version),
  foreign key (tenant_id, fragment_id) references experience.fragment (tenant_id, id)
);
select platform.enable_tenant_rls('experience.fragment_version');
grant select, insert on experience.fragment_version to tb_app;
