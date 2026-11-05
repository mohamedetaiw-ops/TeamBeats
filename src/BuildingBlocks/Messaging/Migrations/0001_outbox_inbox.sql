-- Transactional outbox, consumer inbox and dead letters (ADR-0006).
-- These are infrastructure tables listed in docs/architecture/rls-exceptions.md: rows carry tenant_id,
-- but only the messaging building block reads them, and the dispatcher must see every tenant's rows.
create schema if not exists messaging;
grant usage on schema messaging to tb_app;

create table messaging.outbox (
  seq bigint generated always as identity primary key,
  message_id uuid not null unique,
  message_type text not null,
  tenant_id uuid null,
  correlation_id text null,
  occurred_at timestamptz not null,
  payload jsonb not null,
  dispatched_at timestamptz null,
  attempts int not null default 0,
  last_error text null
);
create index outbox_pending on messaging.outbox (seq) where dispatched_at is null;
grant select, insert, update on messaging.outbox to tb_app;

create table messaging.inbox (
  consumer text not null,
  message_id uuid not null,
  tenant_id uuid null,
  processed_at timestamptz not null default now(),
  primary key (consumer, message_id)
);
grant select, insert on messaging.inbox to tb_app;

create table messaging.dead_letter (
  id bigint generated always as identity primary key,
  consumer text not null,
  message_id uuid not null,
  message_type text not null,
  tenant_id uuid null,
  correlation_id text null,
  payload jsonb not null,
  reason text not null,
  failed_at timestamptz not null default now()
);
grant select, insert on messaging.dead_letter to tb_app;
