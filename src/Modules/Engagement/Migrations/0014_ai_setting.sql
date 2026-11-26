-- Spec 023 R3 FR-032: whether AI may read anonymous content (Speak Up posts) for summaries and sentiment. Off until an
-- engagement administrator turns it on; one row per tenant, versioned for If-Match.
create table engagement.ai_setting (
  tenant_id uuid not null,
  anonymous_content boolean not null default false,
  version int not null default 1,
  updated_by uuid not null,
  updated_at timestamptz not null,
  primary key (tenant_id)
);
select platform.enable_tenant_rls('engagement.ai_setting');
grant select, insert, update on engagement.ai_setting to tb_app;
