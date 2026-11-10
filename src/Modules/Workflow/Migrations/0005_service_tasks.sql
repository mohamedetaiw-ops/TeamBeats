-- Spec 024 (R2 part) and spec 017 T009: service tasks that call connector actions, and incidents. A service task is one
-- connector step of an instance; its idempotency key is fixed per instance and step, so a retry never acts twice where
-- the system supports it. An incident stops the instance on the step until an operator retries, skips or cancels.
-- Tenant-owned, forced RLS. An instance an operator cancels ends with status 'cancelled'.
alter table workflow.process_instance drop constraint process_instance_status_check;
alter table workflow.process_instance add constraint process_instance_status_check check (status in ('active', 'completed', 'withdrawn', 'cancelled'));

create table workflow.service_task (
  tenant_id uuid not null,
  id uuid not null,
  instance_id uuid not null,
  element_id text not null,
  step_index int not null,
  idempotency_key text not null,
  run_id uuid null,
  status text not null check (status in ('running', 'succeeded', 'business_error', 'incident', 'skipped', 'cancelled')),
  external_reference text null check (external_reference is null or length(external_reference) <= 500),
  error_class text null,
  error_code text null,
  started_at timestamptz not null default now(),
  ended_at timestamptz null,
  primary key (tenant_id, id),
  foreign key (tenant_id, instance_id) references workflow.process_instance (tenant_id, id)
);
create unique index service_task_step on workflow.service_task (tenant_id, instance_id, element_id);
select platform.enable_tenant_rls('workflow.service_task');
grant select, insert, update on workflow.service_task to tb_app;

create table workflow.incident (
  tenant_id uuid not null,
  id uuid not null,
  instance_id uuid not null,
  service_task_id uuid not null,
  element_id text not null,
  error_class text not null,
  error_code text null,
  status text not null check (status in ('open', 'resolved')),
  resolution text null check (resolution is null or resolution in ('retried', 'skipped', 'cancelled')),
  reason text null check (reason is null or length(reason) <= 2000),
  resolved_by uuid null,
  raised_at timestamptz not null default now(),
  resolved_at timestamptz null,
  primary key (tenant_id, id),
  foreign key (tenant_id, instance_id) references workflow.process_instance (tenant_id, id),
  foreign key (tenant_id, service_task_id) references workflow.service_task (tenant_id, id)
);
create index incident_instance on workflow.incident (tenant_id, instance_id);
create unique index incident_open on workflow.incident (tenant_id, service_task_id) where status = 'open';
select platform.enable_tenant_rls('workflow.incident');
grant select, insert, update on workflow.incident to tb_app;
