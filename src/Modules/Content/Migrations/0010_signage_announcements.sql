-- Spec 015 US7 (R2): an announcement marked for signage shows on the screens of the locations it targets. The mark is a
-- setting of the item like pinned and featured, not part of a version.
alter table content.content_item add column signage boolean not null default false;
create index content_item_signage on content.content_item (tenant_id, published_at desc) where signage and status = 'published';
