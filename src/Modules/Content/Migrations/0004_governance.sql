-- Spec 009 slice 4: owner reviews and retirement on the process engine (US3), and restore (US4).
alter table content.content_item add column owner_review_instance_id uuid;
alter table content.content_item add column retire_instance_id uuid;
alter table content.content_item add column review_overdue_notified_at timestamptz;
alter table content.content_version add column restored_from int;
create index content_item_review_due on content.content_item (tenant_id, review_due_at) where status = 'published' and review_due_at is not null;
