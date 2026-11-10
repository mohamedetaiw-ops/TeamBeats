-- Spec 015 US8 scenario 3 (R2): Slack direct messages through the spec 017 Slack connection. A person turns Slack on
-- per category after linking their Slack account; deliveries on the Slack channel retry for up to 2 hours.
alter table notifications.delivery drop constraint delivery_channel_check;
alter table notifications.delivery add constraint delivery_channel_check check (channel in ('email', 'slack'));
alter table notifications.preference add column slack boolean not null default false;
