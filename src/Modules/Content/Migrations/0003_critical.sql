-- Spec 009 slice 3: critical announcements (FR-006, FR-007). Settings live in content_item.critical. The audience
-- table already holds who an item targets; it gains dismissal for banners. Completion reports are background exports.
alter table content.content_item add column deadline_reminded_at timestamptz;
alter table content.audience_member add column dismissed_at timestamptz;
create index content_item_critical on content.content_item (tenant_id, status) where critical is not null;

create table content.ack_report (
  tenant_id uuid not null,
  id uuid not null,
  item_id uuid not null,
  status text not null check (status in ('pending', 'completed', 'failed')),
  row_count int,
  object_key text,
  requested_by uuid not null,
  created_at timestamptz not null,
  completed_at timestamptz,
  expires_at timestamptz,
  primary key (tenant_id, id),
  foreign key (tenant_id, item_id) references content.content_item (tenant_id, id)
);
select platform.enable_tenant_rls('content.ack_report');
grant select, insert, update on content.ack_report to tb_app;
