-- Spec 032 R2 T030 (FR-057): experience feature controls. A switch turns a component or a template on or off for the whole
-- tenant, an org unit and the units below it, or (components only) an audience. Every change is kept as a version.
create table experience.feature_control (
  tenant_id uuid not null,
  id uuid not null,
  subject_kind text not null check (subject_kind in ('component', 'template')),
  subject_id text not null check (length(subject_id) between 1 and 120),
  org_unit_id uuid null,
  audience_id uuid null,
  enabled boolean not null,
  version int not null check (version >= 1),
  changed_by uuid not null,
  changed_at timestamptz not null,
  primary key (tenant_id, id),
  check (org_unit_id is null or audience_id is null),
  check (audience_id is null or subject_kind = 'component')
);
create unique index feature_control_scope on experience.feature_control (tenant_id, subject_kind, subject_id,
  coalesce(org_unit_id, '00000000-0000-0000-0000-000000000000'::uuid), coalesce(audience_id, '00000000-0000-0000-0000-000000000000'::uuid));
select platform.enable_tenant_rls('experience.feature_control');
grant select, insert, update, delete on experience.feature_control to tb_app;

-- Versions are kept even after a switch is removed (a removal is a version with no value); the application role only adds them.
create table experience.feature_control_version (
  tenant_id uuid not null,
  control_id uuid not null,
  version int not null check (version >= 1),
  enabled boolean null,
  changed_by uuid not null,
  changed_at timestamptz not null,
  primary key (tenant_id, control_id, version)
);
select platform.enable_tenant_rls('experience.feature_control_version');
grant select, insert on experience.feature_control_version to tb_app;
