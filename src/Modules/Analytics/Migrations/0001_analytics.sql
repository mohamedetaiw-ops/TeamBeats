-- Spec 021 T001 (R1): the analytics event pipeline. Analytics is a derived read model (module map): raw events, the
-- aggregates rolled up from them and the tenant analytics policy. Every table is tenant-owned with forced row-level
-- security. People appear only as a pseudonymous key, an HMAC of their user ID with a per-tenant salt, so the same
-- person has different keys in different tenants (FR-003).
create schema analytics;
grant usage on schema analytics to tb_app;

-- The tenant analytics policy (FR-004, FR-014, FR-018). One row per tenant, created with the defaults on first use.
create table analytics.policy (
  tenant_id uuid not null primary key,
  nonessential boolean not null default true,
  raw_retention_months int not null default 13 check (raw_retention_months between 3 and 25),
  min_group_size int not null default 5 check (min_group_size between 5 and 1000),
  individual boolean not null default false check (not individual),
  version int not null default 1,
  updated_at timestamptz not null,
  updated_by uuid null
);
select platform.enable_tenant_rls('analytics.policy');
grant select, insert, update on analytics.policy to tb_app;

-- The per-tenant salt for pseudonymous keys. Never returned by any API.
create table analytics.tenant_key (
  tenant_id uuid not null primary key,
  salt bytea not null check (length(salt) = 32),
  created_at timestamptz not null
);
select platform.enable_tenant_rls('analytics.tenant_key');
grant select, insert on analytics.tenant_key to tb_app;

-- Raw events (section 8: Confidential, pseudonymous, 13 months by default). The sequence orders the aggregation work.
create table analytics.event (
  tenant_id uuid not null,
  seq bigint generated always as identity,
  name text not null,
  occurred_at timestamptz not null,
  received_at timestamptz not null,
  user_key text null check (user_key is null or user_key ~ '^[0-9a-f]{32}$'),
  org_unit_id uuid null,
  location text null,
  channel text not null check (channel in ('web', 'mobile', 'teams', 'server')),
  locale text null,
  fields jsonb not null default '{}'::jsonb,
  primary key (tenant_id, seq)
);
create index event_name_time on analytics.event (tenant_id, name, occurred_at);
create index event_time on analytics.event (tenant_id, occurred_at);
select platform.enable_tenant_rls('analytics.event');
grant select, insert, delete on analytics.event to tb_app;

-- How far the aggregation job has read the tenant's events.
create table analytics.aggregate_state (
  tenant_id uuid not null primary key,
  last_seq bigint not null default 0,
  updated_at timestamptz not null
);
select platform.enable_tenant_rls('analytics.aggregate_state');
grant select, insert, update on analytics.aggregate_state to tb_app;

-- The first time each person read an item, so unique readers outlive raw events. Units are the reader's at that time;
-- the zero UUID stands for no unit.
create table analytics.item_reader (
  tenant_id uuid not null,
  source text not null,
  item_id uuid not null,
  user_key text not null,
  org_unit_id uuid not null,
  first_at timestamptz not null,
  primary key (tenant_id, source, item_id, user_key)
);
select platform.enable_tenant_rls('analytics.item_reader');
grant select, insert, delete on analytics.item_reader to tb_app;

-- Views and new readers of an item per day and unit (section 8: aggregates, kept for the life of the tenant).
create table analytics.item_daily (
  tenant_id uuid not null,
  source text not null,
  item_id uuid not null,
  day date not null,
  org_unit_id uuid not null,
  views int not null default 0,
  readers int not null default 0,
  primary key (tenant_id, source, item_id, day, org_unit_id)
);
select platform.enable_tenant_rls('analytics.item_daily');
grant select, insert, update, delete on analytics.item_daily to tb_app;

-- Active people per ISO week and unit, and for the whole tenant under the all-ones UUID (adoption basics).
create table analytics.adoption_weekly (
  tenant_id uuid not null,
  week date not null,
  org_unit_id uuid not null,
  active_users int not null,
  primary key (tenant_id, week, org_unit_id)
);
select platform.enable_tenant_rls('analytics.adoption_weekly');
grant select, insert, update, delete on analytics.adoption_weekly to tb_app;
