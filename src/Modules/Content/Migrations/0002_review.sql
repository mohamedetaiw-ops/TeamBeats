-- Spec 009 slice 2: the approval route (FR-003). An item in review points at its engine instance; the history of a
-- submission (submitted, approved, returned with a comment, withdrawn) is kept per item and version (US1 scenario 2).
alter table content.content_item add column review_instance_id uuid, add column submitted_by uuid;

create table content.item_event (
  tenant_id uuid not null,
  id uuid not null,
  item_id uuid not null,
  version int not null,
  event text not null check (event in ('submitted', 'approved', 'returned', 'withdrawn', 'schedule_missed')),
  actor_user_id uuid,
  on_behalf_of uuid,
  comment text,
  at timestamptz not null,
  primary key (tenant_id, id),
  foreign key (tenant_id, item_id) references content.content_item (tenant_id, id)
);
create index item_event_item on content.item_event (tenant_id, item_id, at);
select platform.enable_tenant_rls('content.item_event');
grant select, insert on content.item_event to tb_app;
