-- Spec 021 R3 (US9 scenario 8, FR-022): governed experience experiments. A page shows one of its earlier releases to a
-- share of the consenting people in a scope, once a tenant administrator other than the proposer approves it. Nothing
-- is stored per person: the arm comes from the experiment and the person, so stopping leaves no residue.
create table experience.experiment (
  tenant_id uuid not null,
  id uuid not null,
  page_id uuid not null,
  name text not null check (length(name) between 1 and 120),
  variant_release int not null,
  percent int not null check (percent between 1 and 50),
  org_unit_id uuid null,
  status text not null check (status in ('proposed', 'running', 'stopped')),
  proposed_by uuid not null,
  proposed_at timestamptz not null,
  approved_by uuid null,
  started_at timestamptz null,
  stopped_by uuid null,
  stopped_at timestamptz null,
  primary key (tenant_id, id),
  foreign key (tenant_id, page_id) references experience.page (tenant_id, id)
);
-- At most one experiment that is proposed or running per page.
create unique index experiment_open_page on experience.experiment (tenant_id, page_id) where status <> 'stopped';
select platform.enable_tenant_rls('experience.experiment');
grant select, insert, update on experience.experiment to tb_app;

-- A person's agreement to take part in experiments; nobody takes part without it.
create table experience.experiment_consent (
  tenant_id uuid not null,
  user_id uuid not null,
  consented boolean not null,
  updated_at timestamptz not null,
  primary key (tenant_id, user_id)
);
select platform.enable_tenant_rls('experience.experiment_consent');
grant select, insert, update, delete on experience.experiment_consent to tb_app;
