-- Spec 032 R2 T025 (US6 scenario 4, FR-053): scheduled page releases. A schedule keeps the definition the publisher
-- scheduled, goes live at its time after validation again and may expire later, returning the page to the release that
-- was in force before. At most one schedule of a page waits at a time.
create table experience.page_schedule (
  tenant_id uuid not null,
  id uuid not null,
  page_id uuid not null,
  definition jsonb not null,
  reason text null check (length(reason) <= 500),
  go_live_at timestamptz not null,
  expires_at timestamptz null check (expires_at is null or expires_at > go_live_at),
  status text not null check (status in ('scheduled', 'live', 'failed', 'cancelled', 'expired')),
  scheduled_by uuid not null,
  scheduled_at timestamptz not null,
  base_release int null,
  release int null,
  findings jsonb null,
  ended_at timestamptz null,
  primary key (tenant_id, id),
  foreign key (tenant_id, page_id) references experience.page (tenant_id, id)
);
create unique index page_schedule_open on experience.page_schedule (tenant_id, page_id) where status = 'scheduled';
create index page_schedule_due on experience.page_schedule (tenant_id, status, go_live_at);
select platform.enable_tenant_rls('experience.page_schedule');
grant select, insert, update on experience.page_schedule to tb_app;
