-- Spec 023 (R2) slice 7: team and star of the period (US5, FR-014). An engagement administrator opens a period for a
-- person award or a team award, for the whole tenant or an org unit and everything below it. People nominate with a
-- reason, recognition approvers in scope pick the winners, and an announcement draft goes to content (009).
-- Every table is tenant-owned with forced row-level security.

create table engagement.award_period (
  tenant_id uuid not null,
  id uuid not null,
  kind text not null check (kind in ('person', 'team')),
  title text not null check (length(title) between 1 and 200),
  org_unit_id uuid null,
  nominations_close_on date not null,
  status text not null default 'open' check (status in ('open', 'decided')),
  announcement_item_id uuid null,
  created_by uuid not null,
  created_at timestamptz not null,
  decided_by uuid null,
  decided_at timestamptz null,
  primary key (tenant_id, id)
);
create index award_period_recent on engagement.award_period (tenant_id, created_at desc);
select platform.enable_tenant_rls('engagement.award_period');
grant select, insert, update on engagement.award_period to tb_app;

-- One nomination per person per period, replaced when they nominate again. nominee_id is a user or an org unit.
create table engagement.award_nomination (
  tenant_id uuid not null,
  period_id uuid not null,
  nominator_id uuid not null,
  nominee_id uuid not null,
  reason text not null check (length(reason) between 1 and 1000),
  created_at timestamptz not null,
  primary key (tenant_id, period_id, nominator_id),
  foreign key (tenant_id, period_id) references engagement.award_period (tenant_id, id)
);
select platform.enable_tenant_rls('engagement.award_nomination');
grant select, insert, update on engagement.award_nomination to tb_app;

create table engagement.award_winner (
  tenant_id uuid not null,
  period_id uuid not null,
  nominee_id uuid not null,
  primary key (tenant_id, period_id, nominee_id),
  foreign key (tenant_id, period_id) references engagement.award_period (tenant_id, id)
);
select platform.enable_tenant_rls('engagement.award_winner');
grant select, insert on engagement.award_winner to tb_app;
