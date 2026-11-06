-- Spec 033 US4 (R1): brand assets (logo, lockup, favicon, default and sign-in images). Files live in tenant-prefixed
-- object storage and are validated as spec 001 logos are; rows never change once written, and brand versions refer to
-- them as asset:{id}.
create table experience.brand_asset (
  tenant_id uuid not null,
  id uuid not null,
  object_key text not null,
  content_type text not null check (content_type in ('image/png', 'image/jpeg', 'image/svg+xml')),
  bytes int not null check (bytes between 1 and 1048576),
  created_by uuid null,
  created_at timestamptz not null default now(),
  primary key (tenant_id, id)
);
select platform.enable_tenant_rls('experience.brand_asset');
grant select, insert on experience.brand_asset to tb_app;
