-- Spec 005 R2 US4 scenario 3: the contract end date of a subscription. After it the tenant has expiry_grace_days of
-- grace (default 30), then is read only. Null means no end date.
alter table entitlements.subscription add column contract_ends_at timestamptz null;
alter table entitlements.subscription add constraint subscription_grace_days check (expiry_grace_days between 0 and 365);
