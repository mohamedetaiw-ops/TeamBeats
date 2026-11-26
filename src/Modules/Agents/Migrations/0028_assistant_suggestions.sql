-- Spec 014 R3 (US9, FR-023): proactive suggestions in the assistant. The tenant can turn them off for everyone (on by
-- default where the package has them), and each person can turn them off for themselves.
alter table agents.assistant_settings add column suggestions_enabled boolean not null default true;

create table agents.assistant_preference (
  tenant_id uuid not null,
  user_id uuid not null,
  suggestions_on boolean not null,
  updated_at timestamptz not null,
  primary key (tenant_id, user_id)
);
select platform.enable_tenant_rls('agents.assistant_preference');
grant select, insert, update, delete on agents.assistant_preference to tb_app;
