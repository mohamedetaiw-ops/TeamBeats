-- Spec 022 T019 (US4 scenario 4, US5 scenarios 4 and 5, FR-015, FR-017, FR-018): journey steps get a type. An
-- attestation step asks the person to attest to the policies flagged "onboarding required" (spec 025); an access
-- removal step keeps a checklist with one line per connected system, escalated when a line is not confirmed after one
-- working day; an exit interview step records structured reasons for analytics and free text for HR only.

alter table hr.journey_step add column type text not null default 'task' check (type in ('task', 'attestation', 'access_removal', 'exit_interview'));
alter table hr.journey_step add column systems text[] not null default '{}';
alter table hr.journey_step add column opened_at timestamptz null;
create index journey_step_open on hr.journey_step (tenant_id, type) where status = 'open';

create table hr.journey_access_item (
  tenant_id uuid not null,
  id uuid not null,
  journey_id uuid not null,
  step_key text not null,
  position int not null,
  connection_id uuid null,
  name text not null check (length(name) between 1 and 200),
  status text not null check (status in ('pending', 'removed', 'not_applicable')),
  note text null check (note is null or length(note) <= 500),
  confirmed_by uuid null,
  confirmed_at timestamptz null,
  escalated_at timestamptz null,
  created_at timestamptz not null,
  primary key (tenant_id, id),
  foreign key (tenant_id, journey_id, step_key) references hr.journey_step (tenant_id, journey_id, key) on delete cascade
);
create index journey_access_item_step on hr.journey_access_item (tenant_id, journey_id, step_key, position);
create index journey_access_item_pending on hr.journey_access_item (tenant_id, created_at) where status = 'pending' and escalated_at is null;
select platform.enable_tenant_rls('hr.journey_access_item');
grant select, insert, update on hr.journey_access_item to tb_app;

-- One exit interview per journey. reasons are fixed keys (counted with the minimum group size); comment is HR only.
create table hr.exit_interview (
  tenant_id uuid not null,
  journey_id uuid not null,
  step_key text not null,
  user_id uuid not null,
  org_unit_id uuid null,
  declined boolean not null default false,
  reasons text[] not null default '{}',
  primary_reason text null,
  rating int null check (rating between 1 and 5),
  would_return boolean null,
  comment text null check (comment is null or length(comment) <= 2000),
  submitted_by uuid not null,
  submitted_at timestamptz not null,
  primary key (tenant_id, journey_id),
  foreign key (tenant_id, journey_id) references hr.journey (tenant_id, id),
  check (not declined or (cardinality(reasons) = 0 and primary_reason is null and rating is null and would_return is null and comment is null))
);
create index exit_interview_at on hr.exit_interview (tenant_id, submitted_at);
select platform.enable_tenant_rls('hr.exit_interview');
grant select, insert on hr.exit_interview to tb_app;
