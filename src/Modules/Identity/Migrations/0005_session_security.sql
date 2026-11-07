-- Spec 026 (R1): the address a session was created from, when its holder last authenticated (for step-up), and when it
-- was last used (for idle timeouts, written at most once a minute).
alter table identity.user_session add column source_ip inet null;
alter table identity.user_session add column authenticated_at timestamptz null;
alter table identity.user_session add column last_seen_at timestamptz null;
update identity.user_session set authenticated_at = created_at, last_seen_at = created_at where authenticated_at is null;
alter table identity.user_session alter column authenticated_at set default now();
alter table identity.user_session alter column last_seen_at set default now();
create index user_session_recent on identity.user_session (tenant_id, created_at);
