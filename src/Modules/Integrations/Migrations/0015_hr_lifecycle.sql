-- Spec 019 T007, T008: HR lifecycle. Each joiner, assignment change, termination and rehire HR reports is kept as a
-- change with its effective date and the run and record it came from (FR-005, FR-018). A change waits until it is
-- due in the tenant time zone; an assignment to an org code nobody has mapped waits for that mapping (FR-007).
-- Org codes HR uses are listed for an HR administrator to map to units. Tenant-owned, forced RLS.
create table integrations.hr_change (
  tenant_id uuid not null,
  id uuid not null,
  seq bigint generated always as identity,
  record_id text not null,
  user_id uuid,
  kind text not null check (kind in ('hire', 'assignment', 'termination', 'rehire')),
  effective_date date not null,
  due_at timestamptz not null,
  details jsonb not null default '{}',
  status text not null check (status in ('pending', 'unmapped', 'applied', 'cancelled')),
  run_id uuid not null,
  created_at timestamptz not null,
  applied_at timestamptz,
  cancelled_reason text,
  primary key (tenant_id, id)
);
create index hr_change_due on integrations.hr_change (tenant_id, due_at) where status = 'pending';
create index hr_change_record on integrations.hr_change (tenant_id, record_id, kind) where status in ('pending', 'unmapped');
select platform.enable_tenant_rls('integrations.hr_change');
grant select, insert, update, delete on integrations.hr_change to tb_app;

create table integrations.hr_org_code (
  tenant_id uuid not null,
  code text not null check (length(code) between 1 and 100),
  unit_id uuid,
  first_seen_at timestamptz not null,
  last_seen_at timestamptz not null,
  mapped_by uuid,
  mapped_at timestamptz,
  primary key (tenant_id, code)
);
select platform.enable_tenant_rls('integrations.hr_org_code');
grant select, insert, update, delete on integrations.hr_org_code to tb_app;

-- Joiners are prepared this many working days before their start (section 6: default 5, 0 to 30). A rehire reopens the
-- former account only when the tenant allows it (US4 scenario 3).
alter table integrations.hr_settings add column onboarding_lead_days int not null default 5 check (onboarding_lead_days between 0 and 30);
alter table integrations.hr_settings add column rehire_reactivates boolean not null default false;
