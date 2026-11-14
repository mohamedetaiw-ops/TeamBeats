-- Spec 013 US4 (R2) and section 8: call records are kept 25 months. After that a daily job folds them into one row per
-- day, unit, use case, agent, model, provider, classification and outcome, without the person, and deletes them. users
-- is how many different people made the calls of the row, so small groups stay hidden from unit viewers.
create table ai.usage_daily (
  tenant_id uuid not null,
  day date not null,
  unit_id uuid null,
  use_case text not null,
  agent_id uuid null,
  model text null,
  provider text null,
  classification text null,
  decision text not null check (decision in ('served', 'denied')),
  denial text null,
  charged_to text not null,
  requests bigint not null,
  input_tokens bigint not null,
  output_tokens bigint not null,
  cost numeric(18, 6) not null,
  users int not null,
  constraint usage_daily_key unique nulls not distinct (tenant_id, day, unit_id, use_case, agent_id, model, provider, classification, decision, denial, charged_to)
);
create index usage_daily_day on ai.usage_daily (tenant_id, day);
select platform.enable_tenant_rls('ai.usage_daily');
grant select, insert, update on ai.usage_daily to tb_app;
grant delete on ai.usage_record to tb_app;
