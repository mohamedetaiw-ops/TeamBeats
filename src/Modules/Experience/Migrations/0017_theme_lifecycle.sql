-- Spec 033 R2 T014 (US2, FR-012): theme lifecycle. A brand keeps one working draft that employees never see. A publisher
-- publishes it as the next immutable version, schedules it, or an editor submits it for approval on the spec 024 engine.
-- Every published version records who published it and why; a revert publishes an earlier version again as a new one.
create table experience.brand_draft (
  tenant_id uuid not null,
  brand_id uuid not null,
  name text not null check (length(name) between 1 and 80),
  tokens jsonb not null,
  assets jsonb not null,
  strict_contrast boolean not null default false,
  base_version int not null,
  updated_by uuid not null,
  updated_at timestamptz not null,
  primary key (tenant_id, brand_id),
  foreign key (tenant_id, brand_id) references experience.brand (tenant_id, id)
);
select platform.enable_tenant_rls('experience.brand_draft');
grant select, insert, update, delete on experience.brand_draft to tb_app;

-- How each version went live. Versions published before R2 have no row; they count as published on save.
create table experience.brand_release (
  tenant_id uuid not null,
  brand_id uuid not null,
  version int not null,
  published_by uuid null,
  published_at timestamptz not null,
  reason text null check (reason is null or length(reason) <= 500),
  rollback_of int null,
  how text not null check (how in ('direct', 'approved', 'scheduled', 'reverted')),
  primary key (tenant_id, brand_id, version),
  foreign key (tenant_id, brand_id, version) references experience.brand_version (tenant_id, brand_id, version)
);
select platform.enable_tenant_rls('experience.brand_release');
grant select, insert on experience.brand_release to tb_app;

-- A submitted draft, kept as it was sent, and its decision.
create table experience.brand_submission (
  tenant_id uuid not null,
  id uuid not null,
  brand_id uuid not null,
  name text not null,
  tokens jsonb not null,
  assets jsonb not null,
  strict_contrast boolean not null,
  base_version int not null,
  status text not null check (status in ('pending', 'approved', 'returned', 'withdrawn')),
  reason text null check (reason is null or length(reason) <= 500),
  submitted_by uuid not null,
  submitted_at timestamptz not null,
  instance_id uuid null,
  decided_by uuid null,
  decided_at timestamptz null,
  comment text null,
  version int null,
  primary key (tenant_id, id),
  foreign key (tenant_id, brand_id) references experience.brand (tenant_id, id)
);
create unique index brand_submission_pending on experience.brand_submission (tenant_id, brand_id) where status = 'pending';
create index brand_submission_instance on experience.brand_submission (tenant_id, instance_id);
select platform.enable_tenant_rls('experience.brand_submission');
grant select, insert, update on experience.brand_submission to tb_app;

-- A scheduled publish of the draft as it was when scheduled; checked again at the time.
create table experience.brand_schedule (
  tenant_id uuid not null,
  id uuid not null,
  brand_id uuid not null,
  name text not null,
  tokens jsonb not null,
  assets jsonb not null,
  strict_contrast boolean not null,
  go_live_at timestamptz not null,
  status text not null check (status in ('scheduled', 'live', 'failed', 'cancelled')),
  reason text null check (reason is null or length(reason) <= 500),
  scheduled_by uuid not null,
  scheduled_at timestamptz not null,
  version int null,
  failure text null,
  primary key (tenant_id, id),
  foreign key (tenant_id, brand_id) references experience.brand (tenant_id, id)
);
create unique index brand_schedule_waiting on experience.brand_schedule (tenant_id, brand_id) where status = 'scheduled';
create index brand_schedule_due on experience.brand_schedule (go_live_at) where status = 'scheduled';
select platform.enable_tenant_rls('experience.brand_schedule');
grant select, insert, update on experience.brand_schedule to tb_app;
