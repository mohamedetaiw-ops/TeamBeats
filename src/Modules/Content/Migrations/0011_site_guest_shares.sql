-- Spec 009 R3 US10 (FR-025): a site shared read-only with one guest space until it expires. The space's readers read
-- the site's published Public and Internal items; the share grants nothing else. Revoked or expired shares are kept for
-- the record.
create table content.site_guest_share (
  tenant_id uuid not null,
  id uuid not null,
  site_id uuid not null,
  space_id uuid not null,
  expires_at timestamptz not null,
  created_at timestamptz not null,
  created_by uuid not null,
  revoked_at timestamptz null,
  revoked_by uuid null,
  primary key (tenant_id, id),
  foreign key (tenant_id, site_id) references content.site (tenant_id, id)
);
create index site_guest_share_site on content.site_guest_share (tenant_id, site_id);
create index site_guest_share_space on content.site_guest_share (tenant_id, space_id) where revoked_at is null;
select platform.enable_tenant_rls('content.site_guest_share');
grant select, insert, update on content.site_guest_share to tb_app;
