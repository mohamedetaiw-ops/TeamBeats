-- Spec 013 (R1): the AI gateway. The model catalogue and the platform default model set are platform data with no
-- tenant column (reached through platform-host endpoints only for writing). The tenant model policy and proposed route
-- changes are tenant-owned with forced row-level security.
create schema ai;
grant usage on schema ai to tb_app;

-- The platform model catalogue (FR-005). Prices are USD per million tokens. max_classification is the highest
-- classification the platform allows for the model; zero_retention and no_training are the provider's terms, and both
-- are needed before a tenant may allow Confidential content for it (FR-007). status blocked removes it for all tenants.
create table ai.model_catalogue (
  model text primary key check (length(model) between 3 and 120),
  provider text not null check (provider in ('openrouter')),
  names jsonb not null default '{}',
  capabilities text[] not null,
  context_tokens int not null check (context_tokens > 0),
  regions text[] not null default '{}',
  zero_retention boolean not null default false,
  no_training boolean not null default false,
  max_classification text not null default 'internal' check (max_classification in ('public', 'internal', 'confidential')),
  input_price numeric(12, 4) not null default 0,
  output_price numeric(12, 4) not null default 0,
  status text not null default 'active' check (status in ('active', 'deprecated', 'blocked')),
  deprecated_on date null,
  updated_at timestamptz not null default now()
);
grant select, insert, update on ai.model_catalogue to tb_app;

-- The platform default model set: for each use case, the default model followed by its fallbacks. Tenants without
-- ai.model_choice use it as it is (US1 scenario 4); others start from it.
create table ai.platform_route (
  use_case text primary key check (use_case in ('assistant', 'summarize', 'translate', 'draft', 'embed', 'agent', 'evaluation')),
  models text[] not null
);
grant select, insert, update on ai.platform_route to tb_app;

-- Defaults are an editable starting point for the platform operator, who keeps prices and terms current. Every model is
-- treated as keeping data and limited to Internal until the operator records otherwise.
insert into ai.model_catalogue (model, provider, names, capabilities, context_tokens, regions, input_price, output_price) values
  ('openai/gpt-4o-mini', 'openrouter', '{"en": "GPT-4o mini"}', '{chat,tools,structured_output,vision}', 128000, '{us}', 0.15, 0.60),
  ('openai/gpt-4o', 'openrouter', '{"en": "GPT-4o"}', '{chat,tools,structured_output,vision}', 128000, '{us}', 2.50, 10.00),
  ('openai/text-embedding-3-small', 'openrouter', '{"en": "Text embedding 3 small"}', '{embeddings}', 8191, '{us}', 0.02, 0);

insert into ai.platform_route (use_case, models) values
  ('assistant', '{openai/gpt-4o-mini,openai/gpt-4o}'),
  ('summarize', '{openai/gpt-4o-mini,openai/gpt-4o}'),
  ('translate', '{openai/gpt-4o-mini,openai/gpt-4o}'),
  ('draft', '{openai/gpt-4o-mini,openai/gpt-4o}'),
  ('embed', '{openai/text-embedding-3-small}'),
  ('agent', '{openai/gpt-4o-mini,openai/gpt-4o}'),
  ('evaluation', '{openai/gpt-4o,openai/gpt-4o-mini}');

-- The tenant model policy (FR-006). One row per tenant; no row means the platform defaults. document holds the
-- allowlist, routes per use case, models allowed for Confidential, parameter limits, the region rule and the logging
-- setting. version is the optimistic concurrency token sent as the ETag.
create table ai.model_policy (
  tenant_id uuid not null primary key,
  version int not null,
  document jsonb not null,
  updated_by uuid not null,
  updated_at timestamptz not null
);
select platform.enable_tenant_rls('ai.model_policy');
grant select, insert, update on ai.model_policy to tb_app;

-- A policy change held for the release gate (FR-019): the proposed document, the routes before and after, and how it
-- ended. One proposed change at a time per tenant; a newer save replaces a waiting one.
create table ai.route_change (
  tenant_id uuid not null,
  id uuid not null,
  base_version int not null,
  document jsonb not null,
  use_cases text[] not null,
  before jsonb not null,
  after jsonb not null,
  status text not null check (status in ('proposed', 'applied', 'rejected', 'replaced', 'overridden')),
  proposed_by uuid not null,
  proposed_at timestamptz not null,
  decided_at timestamptz null,
  decided_by uuid null,
  override_reason text null,
  failures jsonb not null default '[]',
  primary key (tenant_id, id)
);
create unique index route_change_one_proposed on ai.route_change (tenant_id) where status = 'proposed';
select platform.enable_tenant_rls('ai.route_change');
grant select, insert, update on ai.route_change to tb_app;
