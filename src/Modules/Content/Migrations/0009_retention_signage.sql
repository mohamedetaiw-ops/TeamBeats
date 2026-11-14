-- Spec 009 R2 FR-020: retention per content type, keep versions for a period then delete old versions or the whole
-- (archived) item, with legal hold of spec 011 taking precedence. Nothing is deleted until a tenant sets a period.
alter table content.content_type add column retention_months int null check (retention_months is null or retention_months between 1 and 120);
alter table content.content_type add column retention_action text null check (retention_action is null or retention_action in ('versions', 'item'));
alter table content.content_type add constraint content_type_retention_check check ((retention_months is null) = (retention_action is null));

-- Confirmations stay insert only (FR-007): an item people confirmed is never deleted by retention.
grant delete on content.content_version, content.content_item, content.comment, content.comment_report,
  content.item_event, content.ack_report to tb_app;

-- Spec 009 R2 digital signage (section 4 default): a named read-only feed per screen, opened by a random token kept
-- only as a hash; rotating the token revokes the old address.
create table content.signage_screen (
  tenant_id uuid not null,
  id uuid not null,
  site_id uuid not null,
  name text not null check (length(name) between 1 and 100),
  token_hash text not null,
  created_by uuid not null,
  created_at timestamptz not null,
  rotated_at timestamptz null,
  primary key (tenant_id, id),
  foreign key (tenant_id, site_id) references content.site (tenant_id, id)
);
create unique index signage_screen_token on content.signage_screen (tenant_id, token_hash);
select platform.enable_tenant_rls('content.signage_screen');
grant select, insert, update, delete on content.signage_screen to tb_app;
