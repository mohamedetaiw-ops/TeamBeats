-- Spec 026 R2 (US7, FR-021 to FR-024): compliance projects. The platform control sets (ISO 27001:2022 Annex A, SOC 2
-- trust services criteria, NCA ECC) live in code as versioned JSON; the tenant adds its own frameworks here. A project
-- copies the controls of its framework when it starts, so a later change to the library never changes a project.
-- Projects and evidence are kept 7 years (spec 026 section 7): they are archived, never deleted. Every table is
-- tenant-owned with forced row-level security.
create schema compliance;
grant usage on schema compliance to tb_app;

-- A framework the tenant defines (FR-021). key is "tenant." followed by a slug, so it never clashes with a platform key.
create table compliance.framework (
  tenant_id uuid not null,
  id uuid not null,
  key text not null,
  names jsonb not null,
  version_label text not null check (length(version_label) between 1 and 40),
  controls jsonb not null,
  revision int not null default 1,
  created_by uuid not null,
  created_at timestamptz not null,
  updated_at timestamptz not null,
  primary key (tenant_id, id)
);
select platform.enable_tenant_rls('compliance.framework');
grant select, insert, update on compliance.framework to tb_app;
create unique index framework_by_key on compliance.framework (tenant_id, key);

-- A project (US7 scenario 1): one framework, an optional org unit scope (null is the whole tenant), and an optional
-- guest space it is shared to read-only (FR-024).
create table compliance.project (
  tenant_id uuid not null,
  id uuid not null,
  name text not null check (length(name) between 1 and 120),
  framework_key text not null,
  framework_version text not null,
  framework_names jsonb not null,
  org_unit_id uuid null,
  shared_space_id uuid null,
  status text not null default 'active' check (status in ('active', 'archived')),
  version int not null default 1,
  created_by uuid not null,
  created_at timestamptz not null,
  updated_at timestamptz not null,
  primary key (tenant_id, id)
);
select platform.enable_tenant_rls('compliance.project');
grant select, insert, update on compliance.project to tb_app;

-- A control of a project (FR-022): owner, status, due date and links to policies (025) and documents (011).
create table compliance.project_control (
  tenant_id uuid not null,
  id uuid not null,
  project_id uuid not null,
  position int not null,
  code text not null,
  titles jsonb not null,
  owner_id uuid null,
  status text not null default 'not_started' check (status in ('not_started', 'in_progress', 'evidence_ready', 'reviewed')),
  due_on date null,
  policy_ids uuid[] not null default '{}',
  document_ids uuid[] not null default '{}',
  note text null check (length(note) <= 2000),
  version int not null default 1,
  updated_at timestamptz not null,
  primary key (tenant_id, id),
  foreign key (tenant_id, project_id) references compliance.project (tenant_id, id)
);
select platform.enable_tenant_rls('compliance.project_control');
grant select, insert, update on compliance.project_control to tb_app;
create unique index project_control_by_code on compliance.project_control (tenant_id, project_id, code);
create index project_control_by_owner on compliance.project_control (tenant_id, owner_id);

-- An evidence task of a control (US7 scenario 2), such as "export admin role changes for the last 12 months".
create table compliance.evidence_task (
  tenant_id uuid not null,
  id uuid not null,
  project_id uuid not null,
  control_id uuid not null,
  title text not null check (length(title) between 1 and 200),
  owner_id uuid null,
  due_on date null,
  done_at timestamptz null,
  created_by uuid not null,
  created_at timestamptz not null,
  primary key (tenant_id, id),
  foreign key (tenant_id, control_id) references compliance.project_control (tenant_id, id)
);
select platform.enable_tenant_rls('compliance.evidence_task');
grant select, insert, update, delete on compliance.evidence_task to tb_app;
create index evidence_task_by_control on compliance.evidence_task (tenant_id, control_id);

-- Evidence attached to a task (FR-023): an audit export keeps its export ID and the SHA-256 of the file. The file is
-- copied into the project's storage, since exports themselves expire after 7 days.
create table compliance.evidence (
  tenant_id uuid not null,
  id uuid not null,
  project_id uuid not null,
  task_id uuid not null,
  kind text not null check (kind in ('audit_export')),
  source_id uuid not null,
  format text not null,
  row_count bigint null,
  bytes bigint not null,
  sha256 text not null check (sha256 ~ '^[0-9a-f]{64}$'),
  attached_by uuid not null,
  attached_at timestamptz not null,
  primary key (tenant_id, id),
  foreign key (tenant_id, task_id) references compliance.evidence_task (tenant_id, id)
);
select platform.enable_tenant_rls('compliance.evidence');
grant select, insert on compliance.evidence to tb_app;
create index evidence_by_task on compliance.evidence (tenant_id, task_id);
