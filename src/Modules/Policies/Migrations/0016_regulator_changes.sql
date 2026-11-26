-- Spec 025 R3 US10 (FR-024, FR-025): items read from regulator feed connections (spec 017). Only the item's id, title,
-- https link, date and plain text summary are kept, for 2 years. A compliance officer dismisses an item or links it to
-- policies; a review started from it names it.
create table policies.regulator_feed_read (
  tenant_id uuid not null,
  connection_id uuid not null,
  read_at timestamptz not null,
  result text not null,
  primary key (tenant_id, connection_id)
);
select platform.enable_tenant_rls('policies.regulator_feed_read');
grant select, insert, update on policies.regulator_feed_read to tb_app;

create table policies.regulator_change (
  tenant_id uuid not null,
  id uuid not null,
  connection_id uuid not null,
  source_name text not null check (length(source_name) between 1 and 200),
  item_id text not null check (length(item_id) between 1 and 512),
  title text not null check (length(title) between 1 and 500),
  link text null check (link is null or (link like 'https://%' and length(link) <= 2048)),
  published_at timestamptz null,
  summary text null check (summary is null or length(summary) <= 2000),
  received_at timestamptz not null,
  status text not null default 'new' check (status in ('new', 'dismissed', 'actioned')),
  note text null check (note is null or length(note) <= 2000),
  decided_by uuid null,
  decided_at timestamptz null,
  policy_ids uuid[] not null default '{}',
  primary key (tenant_id, id),
  unique (tenant_id, connection_id, item_id),
  check ((status = 'new') = (decided_at is null))
);
create index regulator_change_status on policies.regulator_change (tenant_id, status, received_at desc);
select platform.enable_tenant_rls('policies.regulator_change');
grant select, insert, update, delete on policies.regulator_change to tb_app;

alter table policies.policy_review add column regulator_change_id uuid null;
