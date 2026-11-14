-- Spec 011 R2 FR-024 and FR-025: the SharePoint sites an integration administrator allows for a Microsoft 365
-- connection's SharePoint documents feature. TeamBeats reads only these, even when Microsoft grants the application more.
create table integrations.sharepoint_site (
  tenant_id uuid not null,
  connection_id uuid not null,
  site_id text not null,
  display_name text not null,
  web_url text not null,
  added_by uuid not null,
  added_at timestamptz not null,
  primary key (tenant_id, connection_id, site_id),
  foreign key (tenant_id, connection_id) references integrations.connection (tenant_id, id) on delete cascade
);
select platform.enable_tenant_rls('integrations.sharepoint_site');
grant select, insert, delete on integrations.sharepoint_site to tb_app;
