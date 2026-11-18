-- Spec 021 R2 T016 (US7, FR-011, FR-012): scheduled reports and exports. A schedule names one dashboard and its filters
-- (section 8: Internal, owner ID only) and is sent weekly or monthly to its owner or to the members of one unit. Each
-- send and each export is one report row per recipient, computed in the background as that recipient, so a recipient
-- only ever holds the figures they could open themselves at send time.
create table analytics.report_schedule (
  tenant_id uuid not null,
  id uuid not null,
  owner_user_id uuid not null,
  name text not null check (length(name) between 1 and 120),
  definition jsonb not null,
  cadence text not null check (cadence in ('weekly', 'monthly')),
  recipient_unit_id uuid null,
  next_run_at timestamptz not null,
  last_run_at timestamptz null,
  created_at timestamptz not null,
  primary key (tenant_id, id)
);
create index report_schedule_due on analytics.report_schedule (tenant_id, next_run_at);
create index report_schedule_owner on analytics.report_schedule (tenant_id, owner_user_id);
select platform.enable_tenant_rls('analytics.report_schedule');
grant select, insert, update, delete on analytics.report_schedule to tb_app;

create table analytics.report (
  tenant_id uuid not null,
  id uuid not null,
  recipient_user_id uuid not null,
  schedule_id uuid null,
  owner_user_id uuid not null,
  name text not null check (length(name) between 1 and 120),
  definition jsonb not null,
  period_from date null,
  period_to date null,
  format text null check (format is null or format in ('csv', 'xlsx')),
  status text not null check (status in ('queued', 'ready', 'skipped', 'failed')),
  result jsonb null,
  created_at timestamptz not null,
  done_at timestamptz null,
  primary key (tenant_id, id)
);
create index report_queued on analytics.report (tenant_id, status, created_at) where status = 'queued';
create index report_recipient on analytics.report (tenant_id, recipient_user_id, created_at desc);
create index report_age on analytics.report (tenant_id, created_at);
select platform.enable_tenant_rls('analytics.report');
grant select, insert, update, delete on analytics.report to tb_app;
