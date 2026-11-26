-- Spec 032 R3 FR-031 (ADR-0012 point 6): components a tenant builds itself, run only in a sandboxed frame. Each version
-- is immutable and waits for the platform's security review; only an approved version can be enabled and shown.
create table experience.tenant_component (
  tenant_id uuid not null,
  id text not null,
  names jsonb not null,
  enabled boolean not null default false,
  created_by uuid not null,
  created_at timestamptz not null,
  changed_at timestamptz not null,
  primary key (tenant_id, id)
);
select platform.enable_tenant_rls('experience.tenant_component');
grant select, insert, update on experience.tenant_component to tb_app;

create table experience.tenant_component_version (
  tenant_id uuid not null,
  component_id text not null,
  version int not null,
  hosts text[] not null,
  context text[] not null,
  events text[] not null,
  height int not null check (height between 80 and 1200),
  document text not null,
  sha256 text not null,
  status text not null check (status in ('in_review', 'approved', 'rejected')),
  submitted_by uuid not null,
  submitted_at timestamptz not null,
  reviewed_by uuid null,
  reviewed_at timestamptz null,
  review_note text null,
  primary key (tenant_id, component_id, version),
  foreign key (tenant_id, component_id) references experience.tenant_component (tenant_id, id)
);
select platform.enable_tenant_rls('experience.tenant_component_version');
grant select, insert, update on experience.tenant_component_version to tb_app;
