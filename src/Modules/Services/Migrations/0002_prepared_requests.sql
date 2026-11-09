-- Spec 020 T013 (R1): requests an agent prepared for the person (020 US8 scenario 1, FR-013). A prepared request is a
-- draft the person sees in the task inbox marked "prepared by AI"; nothing is submitted until they submit it themselves.
-- Tenant-owned with forced RLS.
create table services.prepared_request (
  tenant_id uuid not null,
  id uuid not null,
  user_id uuid not null,
  service_id uuid not null,
  data jsonb not null default '{}'::jsonb,
  agent_id uuid not null,
  run_id uuid not null,
  idempotency_key text not null,
  status text not null check (status in ('prepared', 'submitted', 'discarded')),
  request_id uuid null,
  created_at timestamptz not null,
  updated_at timestamptz not null,
  primary key (tenant_id, id)
);
create unique index prepared_request_key on services.prepared_request (tenant_id, user_id, idempotency_key);
create index prepared_request_user on services.prepared_request (tenant_id, user_id, status, created_at desc);
select platform.enable_tenant_rls('services.prepared_request');
grant select, insert, update on services.prepared_request to tb_app;
