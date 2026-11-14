-- Spec 013 US5 and FR-012 (R2): budgets per tenant, org unit, agent and user. A budget is a USD amount or a request
-- count per month or per day. each_user is one limit that applies to every person on their own (the per-user daily
-- limit of US5 scenario 3). The action decides what happens once the limit is reached: block, downgrade to a named
-- cheaper model, or alert only.
create table ai.budget (
  tenant_id uuid not null,
  id uuid not null,
  scope text not null check (scope in ('tenant', 'org_unit', 'agent', 'user', 'each_user')),
  scope_id uuid null,
  period text not null check (period in ('month', 'day')),
  amount numeric(14, 2) null check (amount > 0),
  requests int null check (requests > 0),
  action text not null check (action in ('block', 'downgrade', 'alert')),
  downgrade_model text null,
  owner_id uuid null,
  version int not null default 1,
  created_by uuid not null,
  created_at timestamptz not null,
  updated_at timestamptz not null,
  primary key (tenant_id, id),
  check ((amount is null) <> (requests is null)),
  check ((scope in ('tenant', 'each_user')) = (scope_id is null)),
  check ((action = 'downgrade') = (downgrade_model is not null))
);
create unique index budget_one_per_scope on ai.budget (tenant_id, scope, coalesce(scope_id, '00000000-0000-0000-0000-000000000000'), period, (amount is null));
select platform.enable_tenant_rls('ai.budget');
grant select, insert, update, delete on ai.budget to tb_app;

-- Requests counted against request budgets as each call is planned, so concurrent calls cannot all pass the last
-- request (spec 005 tolerance). subject is the person for each_user budgets, else the zero UUID. A call that is denied
-- or fails after planning gives its request back.
create table ai.budget_use (
  tenant_id uuid not null,
  budget_id uuid not null,
  subject uuid not null,
  period_start timestamptz not null,
  requests int not null,
  primary key (tenant_id, budget_id, subject, period_start)
);
select platform.enable_tenant_rls('ai.budget_use');
grant select, insert, update, delete on ai.budget_use to tb_app;

-- One row per budget, subject, period and threshold (80 or 100), so each alert is sent once.
create table ai.budget_alert (
  tenant_id uuid not null,
  budget_id uuid not null,
  subject uuid not null,
  period_start timestamptz not null,
  threshold int not null check (threshold in (80, 100)),
  at timestamptz not null,
  primary key (tenant_id, budget_id, subject, period_start, threshold)
);
select platform.enable_tenant_rls('ai.budget_alert');
grant select, insert, delete on ai.budget_alert to tb_app;
