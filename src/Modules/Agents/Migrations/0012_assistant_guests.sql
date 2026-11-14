-- Spec 014 R2 (AST-022): guests may use the assistant only when the tenant turns it on, and then retrieval runs as
-- the guest, so only the spaces they were invited to can be cited. Off by default.
alter table agents.assistant_settings add column guests_allowed boolean not null default false;
