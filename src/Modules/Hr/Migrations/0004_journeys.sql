-- Spec 022 slice 3 (US4, US5, FR-013, FR-014): journey templates with steps relative to an anchor date, the journeys
-- started from HR, directory and service events, and their steps, each run as a one step process on spec 024.

create table hr.journey_template (
  tenant_id uuid not null,
  id uuid not null,
  key text not null check (key ~ '^[a-z][a-z0-9_]{1,39}$'),
  kind text not null check (kind in ('onboarding', 'offboarding', 'transfer', 'promotion')),
  names jsonb not null,
  org_unit_id uuid null,
  start_on text not null check (start_on in ('hire', 'account', 'termination', 'service', 'manual')),
  service_id uuid null,
  anchor_field text null,
  steps jsonb not null,
  active boolean not null default true,
  version int not null default 1,
  created_at timestamptz not null,
  updated_at timestamptz not null,
  updated_by uuid null,
  primary key (tenant_id, id),
  unique (tenant_id, key),
  check ((start_on = 'service') = (service_id is not null))
);
select platform.enable_tenant_rls('hr.journey_template');
grant select, insert, update on hr.journey_template to tb_app;

-- One active journey per template and person: a second event for the same person moves the anchor instead. A re-hire's
-- journey links the earlier one (edge cases).
create table hr.journey (
  tenant_id uuid not null,
  id uuid not null,
  template_id uuid not null,
  template_version int not null,
  kind text not null,
  user_id uuid not null,
  buddy_user_id uuid null,
  org_unit_id uuid null,
  anchor_date date not null,
  status text not null check (status in ('active', 'completed', 'cancelled')),
  started_by_event text not null,
  source_key text not null,
  cancel_reason text null,
  previous_journey_id uuid null,
  created_at timestamptz not null,
  updated_at timestamptz not null,
  completed_at timestamptz null,
  primary key (tenant_id, id),
  foreign key (tenant_id, template_id) references hr.journey_template (tenant_id, id),
  unique (tenant_id, template_id, source_key)
);
create unique index journey_active on hr.journey (tenant_id, template_id, user_id) where status = 'active';
create index journey_user on hr.journey (tenant_id, user_id, created_at);
create index journey_status on hr.journey (tenant_id, status, anchor_date);
select platform.enable_tenant_rls('hr.journey');
grant select, insert, update on hr.journey to tb_app;

create table hr.journey_step (
  tenant_id uuid not null,
  journey_id uuid not null,
  key text not null,
  position int not null,
  names jsonb not null,
  offset_days int not null,
  assignee text not null check (assignee in ('person', 'buddy', 'manager', 'users', 'permission')),
  users uuid[] not null default '{}',
  permission text null,
  due_working_days int null,
  scheduled_on date not null,
  status text not null check (status in ('scheduled', 'open', 'done', 'cancelled')),
  instance_id uuid null,
  task_id uuid null,
  due_at timestamptz null,
  outcome text null,
  completed_by uuid null,
  completed_at timestamptz null,
  primary key (tenant_id, journey_id, key),
  foreign key (tenant_id, journey_id) references hr.journey (tenant_id, id) on delete cascade
);
create index journey_step_due on hr.journey_step (tenant_id, scheduled_on) where status = 'scheduled';
select platform.enable_tenant_rls('hr.journey_step');
grant select, insert, update on hr.journey_step to tb_app;
