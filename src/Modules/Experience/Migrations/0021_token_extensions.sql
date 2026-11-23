-- Spec 033 R2 T022 (US8 scenario 3, FR-013): reviewed token extensions. A theme editor proposes an extra token, such as
-- a campaign accent, for the tenant or one organization; another person who may publish themes there approves it before
-- it reaches anyone. An extension lives in its own ext. namespace, so it can never replace a platform token or a
-- protected behavior, and its value is typed like any token, so free CSS cannot enter.
create table experience.token_extension (
  tenant_id uuid not null,
  id uuid not null,
  -- Null for the tenant; an organization's extension applies there and below only.
  scope_org_unit_id uuid,
  key text not null check (key ~ '^[a-z][a-z0-9_]{1,31}$'),
  type text not null check (type in ('color', 'dimension', 'number', 'duration', 'shadow')),
  value jsonb not null,
  description text,
  state text not null check (state in ('proposed', 'approved', 'rejected')),
  proposed_by uuid not null,
  proposed_at timestamptz not null,
  reviewed_by uuid,
  reviewed_at timestamptz,
  review_note text,
  primary key (tenant_id, id)
);
create unique index token_extension_key on experience.token_extension
  (tenant_id, coalesce(scope_org_unit_id, '00000000-0000-0000-0000-000000000000'::uuid), key);
select platform.enable_tenant_rls('experience.token_extension');
grant select, insert, update, delete on experience.token_extension to tb_app;
