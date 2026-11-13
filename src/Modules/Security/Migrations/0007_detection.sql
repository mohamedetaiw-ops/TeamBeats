-- Spec 026 R2 (US6, FR-015): sensitive data detection. The tenant turns it on, sets a mode (off, warn, block) per
-- location type, and adds its own patterns, each tested against sample text before it is enabled. Samples are not kept.
alter table security.policy
  add column detection_enabled boolean not null default false,
  add column detection_modes jsonb not null default '{}'::jsonb;

create table security.detection_pattern (
  tenant_id uuid not null,
  id uuid not null,
  name text not null check (length(name) between 1 and 80),
  pattern text not null check (length(pattern) between 1 and 500),
  enabled boolean not null default false,
  tested_at timestamptz null,
  created_by uuid not null,
  created_at timestamptz not null,
  updated_at timestamptz not null,
  primary key (tenant_id, id)
);
select platform.enable_tenant_rls('security.detection_pattern');
grant select, insert, update, delete on security.detection_pattern to tb_app;
create unique index detection_pattern_name on security.detection_pattern (tenant_id, lower(name));
