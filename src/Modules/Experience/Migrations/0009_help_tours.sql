-- Spec 008 R2 T024 (FR-020): in-context help hints and short tours as versioned tenant configuration, and each person's
-- dismissals. A version is never changed once published; the latest is live.
create table experience.help_config (
  tenant_id uuid not null,
  version int not null,
  definition jsonb not null,
  published_by uuid null,
  published_at timestamptz not null default now(),
  primary key (tenant_id, version)
);
select platform.enable_tenant_rls('experience.help_config');
grant select, insert on experience.help_config to tb_app;

create table experience.help_dismissal (
  tenant_id uuid not null,
  user_id uuid not null,
  item_key text not null check (item_key ~ '^[a-z0-9_-]{1,60}$'),
  dismissed_at timestamptz not null,
  primary key (tenant_id, user_id, item_key)
);
select platform.enable_tenant_rls('experience.help_dismissal');
grant select, insert, update, delete on experience.help_dismissal to tb_app;
