-- Spec 009 FR-018 R3: an event item links one company event of the calendar (spec 037) by ID. Only the link is kept
-- here; the event's times, place and registration stay in the calendar, so an event exists once.
create table content.item_calendar_event (
  tenant_id uuid not null,
  item_id uuid not null,
  event_id uuid not null,
  primary key (tenant_id, item_id),
  foreign key (tenant_id, item_id) references content.content_item (tenant_id, id)
);
create index item_calendar_event_event on content.item_calendar_event (tenant_id, event_id);
select platform.enable_tenant_rls('content.item_calendar_event');
grant select, insert, update, delete on content.item_calendar_event to tb_app;
