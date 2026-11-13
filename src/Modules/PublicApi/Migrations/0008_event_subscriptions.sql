-- Spec 035 FR-018, US4 (slice 5b): event subscriptions of application apps. Each one sends one event type of the public
-- event catalogue to one HTTPS URL, signed with a tenant secret manager reference, through an endpoint of the spec 017
-- delivery runtime (endpoint_id, owned by Integrations; its delivery log stays there). After 3 days of failed
-- deliveries the subscription is paused until an API administrator resumes it. Tenant-owned, forced RLS.
create table publicapi.event_subscription (
  tenant_id uuid not null,
  id uuid not null,
  app_id uuid not null,
  event_type text not null check (length(event_type) between 1 and 64),
  url text not null check (length(url) between 9 and 2000),
  secret_ref text not null check (length(secret_ref) between 1 and 200),
  endpoint_id uuid not null,
  status text not null check (status in ('active', 'paused')),
  failing_since timestamptz null,
  paused_at timestamptz null,
  created_by uuid not null,
  created_at timestamptz not null default now(),
  primary key (tenant_id, id),
  unique (tenant_id, app_id, event_type, url),
  unique (tenant_id, endpoint_id),
  foreign key (tenant_id, app_id) references publicapi.app (tenant_id, id) on delete cascade
);
create index event_subscription_type on publicapi.event_subscription (tenant_id, event_type) where status = 'active';
select platform.enable_tenant_rls('publicapi.event_subscription');
grant select, insert, update, delete on publicapi.event_subscription to tb_app;
