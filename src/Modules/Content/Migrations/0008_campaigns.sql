-- Spec 009 R2 US8, FR-015: campaigns group items of one site into steps sent on a schedule to one audience through
-- spec 015, with a send log per recipient (kept 2 years by default, section 13) for "only those who have not acted"
-- and the dashboard counts.
create table content.campaign (
  tenant_id uuid not null,
  id uuid not null,
  site_id uuid not null,
  name text not null check (length(name) between 1 and 200),
  audience jsonb not null,
  status text not null check (status in ('draft', 'scheduled', 'completed', 'cancelled')),
  created_by uuid not null,
  created_at timestamptz not null,
  updated_at timestamptz not null,
  row_version int not null default 1,
  primary key (tenant_id, id),
  foreign key (tenant_id, site_id) references content.site (tenant_id, id)
);
select platform.enable_tenant_rls('content.campaign');
grant select, insert, update on content.campaign to tb_app;

create table content.campaign_step (
  tenant_id uuid not null,
  id uuid not null,
  campaign_id uuid not null,
  position int not null check (position between 1 and 20),
  item_id uuid not null,
  at timestamptz not null,
  mandatory boolean not null default false,
  only_not_acted boolean not null default false,
  status text not null check (status in ('pending', 'sent', 'skipped', 'cancelled')),
  sent_at timestamptz null,
  recipients int null,
  delivered int null,
  primary key (tenant_id, id),
  unique (tenant_id, campaign_id, position),
  foreign key (tenant_id, campaign_id) references content.campaign (tenant_id, id),
  foreign key (tenant_id, item_id) references content.content_item (tenant_id, id)
);
create index campaign_step_due on content.campaign_step (tenant_id, at) where status = 'pending';
select platform.enable_tenant_rls('content.campaign_step');
grant select, insert, update, delete on content.campaign_step to tb_app;

create table content.campaign_send (
  tenant_id uuid not null,
  step_id uuid not null,
  campaign_id uuid not null,
  item_id uuid not null,
  user_id uuid not null,
  sent_at timestamptz not null,
  viewed_at timestamptz null,
  clicked_at timestamptz null,
  primary key (tenant_id, step_id, user_id)
);
create index campaign_send_by_reader on content.campaign_send (tenant_id, user_id, item_id);
create index campaign_send_by_campaign on content.campaign_send (tenant_id, campaign_id);
select platform.enable_tenant_rls('content.campaign_send');
grant select, insert, update on content.campaign_send to tb_app;
