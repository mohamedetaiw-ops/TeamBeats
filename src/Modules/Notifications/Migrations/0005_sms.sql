-- Spec 015 US6 scenario 2 (R2): SMS for mandatory messages to people who have neither email nor the app, through
-- the tenant's SMS gateway connection (spec 017). At most one SMS per message and person (FR-020): one delivery row,
-- sent once by its idempotency key. The number is read from the directory when the SMS is sent and is never stored here.
alter table notifications.delivery drop constraint delivery_channel_check;
alter table notifications.delivery add constraint delivery_channel_check check (channel in ('email', 'slack', 'push', 'teams', 'sms'));
create unique index delivery_one_sms on notifications.delivery (tenant_id, notification_id, user_id) where channel = 'sms';
