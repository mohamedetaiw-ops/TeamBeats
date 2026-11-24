-- Spec 015 US5 (R2): Microsoft Teams activity notifications through the Microsoft 365 connection (spec 006). A person
-- turns Teams on per category; mandatory categories go to Teams whenever the tenant has it. A Teams delivery that
-- cannot reach the person (app not installed, no Microsoft 365 account, connection off) falls back to email and the
-- delivery row keeps why.
alter table notifications.delivery drop constraint delivery_channel_check;
alter table notifications.delivery add constraint delivery_channel_check check (channel in ('email', 'slack', 'push', 'teams'));
alter table notifications.preference add column teams boolean not null default false;
