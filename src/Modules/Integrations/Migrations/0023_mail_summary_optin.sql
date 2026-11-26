-- Spec 018 R3 (US6, FR-022): the employee's own opt-in to mail summaries by the assistant. Nothing about the mail is
-- kept; a row only says that the employee said yes, and it goes when they say no or their membership is disabled.
create table integrations.mail_summary_optin (
  tenant_id uuid not null,
  user_id uuid not null,
  opted_in_at timestamptz not null default now(),
  primary key (tenant_id, user_id)
);
select platform.enable_tenant_rls('integrations.mail_summary_optin');
grant select, insert, update, delete on integrations.mail_summary_optin to tb_app;
