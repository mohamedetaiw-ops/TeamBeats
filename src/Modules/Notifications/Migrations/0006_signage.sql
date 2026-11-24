-- Spec 015 US7 (R2, FR-021): signage screens of a location. A screen sits at one org unit (the location in the
-- organization, which is also its authorization scope) and may name the location people's profiles use; it opens its
-- feed with a random token kept only as a SHA-256 hash. Revoking a screen deletes the row, so its address stops at once.
create table notifications.signage_screen (
  tenant_id uuid not null,
  id uuid not null,
  name text not null check (length(name) between 1 and 100),
  org_unit_id uuid not null,
  location text null check (location is null or length(location) between 1 and 100),
  token_hash text not null,
  created_by uuid not null,
  created_at timestamptz not null,
  rotated_at timestamptz null,
  last_seen_at timestamptz null,
  primary key (tenant_id, id)
);
create unique index signage_screen_token on notifications.signage_screen (tenant_id, token_hash);
create index signage_screen_unit on notifications.signage_screen (tenant_id, org_unit_id);
select platform.enable_tenant_rls('notifications.signage_screen');
grant select, insert, update, delete on notifications.signage_screen to tb_app;
