-- Spec 016 (R1): service catalogue, form versions, requests with their Tracking events and attachments, favourites,
-- recent use and delegations. Every table is tenant-owned with forced row-level security.
create schema services;
grant usage on schema services to tb_app;

create table services.category (
  tenant_id uuid not null,
  id uuid not null,
  names jsonb not null,
  position int not null default 0,
  created_at timestamptz not null default now(),
  primary key (tenant_id, id)
);
select platform.enable_tenant_rls('services.category');
grant select, insert, update, delete on services.category to tb_app;

create table services.service (
  tenant_id uuid not null,
  id uuid not null,
  kind text not null check (kind in ('form', 'linked', 'module')),
  module_key text null,
  names jsonb not null,
  descriptions jsonb not null default '{}'::jsonb,
  synonyms jsonb not null default '{}'::jsonb,
  category_id uuid null,
  audience jsonb not null default '{}'::jsonb,
  owner_org_unit_id uuid null,
  classification text not null default 'internal',
  url text null,
  sso boolean not null default false,
  launch_route text null,
  status text not null default 'draft' check (status in ('draft', 'published', 'retired')),
  current_version int null,
  created_at timestamptz not null default now(),
  created_by uuid null,
  updated_at timestamptz not null default now(),
  primary key (tenant_id, id),
  foreign key (tenant_id, category_id) references services.category (tenant_id, id)
);
create unique index service_module_key on services.service (tenant_id, module_key) where module_key is not null;
select platform.enable_tenant_rls('services.service');
grant select, insert, update on services.service to tb_app;

create table services.service_version (
  tenant_id uuid not null,
  service_id uuid not null,
  version int not null,
  state text not null check (state in ('draft', 'published')),
  form jsonb not null default '{"fields": []}'::jsonb,
  route jsonb not null default '{}'::jsonb,
  fulfilment_queue_group_id uuid null,
  allow_on_behalf boolean not null default false,
  max_attachments int not null default 0,
  updated_at timestamptz not null default now(),
  updated_by uuid null,
  published_at timestamptz null,
  published_by uuid null,
  primary key (tenant_id, service_id, version),
  foreign key (tenant_id, service_id) references services.service (tenant_id, id)
);
select platform.enable_tenant_rls('services.service_version');
grant select, insert, update on services.service_version to tb_app;

create table services.request (
  tenant_id uuid not null,
  id uuid not null,
  number text not null,
  service_id uuid not null,
  service_version int not null,
  requester_user_id uuid not null,
  on_behalf_of_user_id uuid null,
  requester_org_unit_id uuid null,
  status text not null check (status in ('draft', 'submitted', 'in_approval', 'returned', 'approved', 'rejected', 'cancelled', 'completed')),
  data jsonb not null default '{}'::jsonb,
  classification text not null default 'internal',
  process_instance_id uuid null,
  idempotency_key text null,
  created_at timestamptz not null default now(),
  submitted_at timestamptz null,
  closed_at timestamptz null,
  primary key (tenant_id, id),
  unique (tenant_id, number),
  foreign key (tenant_id, service_id, service_version) references services.service_version (tenant_id, service_id, version)
);
create unique index request_idempotency on services.request (tenant_id, requester_user_id, idempotency_key) where idempotency_key is not null;
create index request_requester on services.request (tenant_id, requester_user_id, created_at desc);
select platform.enable_tenant_rls('services.request');
grant select, insert, update on services.request to tb_app;

create table services.request_event (
  tenant_id uuid not null,
  id bigint generated always as identity,
  request_id uuid not null,
  event text not null,
  step text null,
  actor_id uuid null,
  on_behalf_of uuid null,
  detail jsonb not null default '{}'::jsonb,
  at timestamptz not null default now(),
  primary key (tenant_id, id)
);
create index request_event_request on services.request_event (tenant_id, request_id, id);
select platform.enable_tenant_rls('services.request_event');
grant select, insert on services.request_event to tb_app;

create table services.attachment (
  tenant_id uuid not null,
  id uuid not null,
  request_id uuid null,
  uploaded_by uuid not null,
  object_key text not null,
  file_name text not null,
  size_bytes bigint not null,
  content_type text not null,
  scan_state text not null default 'pending' check (scan_state in ('pending', 'clean', 'infected', 'failed')),
  created_at timestamptz not null default now(),
  primary key (tenant_id, id)
);
select platform.enable_tenant_rls('services.attachment');
grant select, insert, update, delete on services.attachment to tb_app;

create table services.favourite (
  tenant_id uuid not null,
  user_id uuid not null,
  service_id uuid not null,
  created_at timestamptz not null default now(),
  primary key (tenant_id, user_id, service_id)
);
select platform.enable_tenant_rls('services.favourite');
grant select, insert, delete on services.favourite to tb_app;

create table services.recent_use (
  tenant_id uuid not null,
  user_id uuid not null,
  service_id uuid not null,
  used_at timestamptz not null,
  primary key (tenant_id, user_id, service_id)
);
select platform.enable_tenant_rls('services.recent_use');
grant select, insert, update, delete on services.recent_use to tb_app;

create table services.delegation (
  tenant_id uuid not null,
  user_id uuid not null,
  delegate_user_id uuid not null,
  starts_on date not null,
  ends_on date not null,
  updated_at timestamptz not null default now(),
  primary key (tenant_id, user_id),
  check (ends_on >= starts_on),
  check (delegate_user_id <> user_id)
);
create index delegation_delegate on services.delegation (tenant_id, delegate_user_id);
select platform.enable_tenant_rls('services.delegation');
grant select, insert, update, delete on services.delegation to tb_app;
