-- Spec 026 US8: debug log entries of a tenant with a customer-managed key are encrypted with its own data key.
alter table ai.debug_log add column data_key_id uuid null;
