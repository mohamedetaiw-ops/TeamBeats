-- Spec 026 FR-016: four-eyes approval of critical tenant settings. The tenant switch, and the changes held for a second
-- administrator with the request to apply, the fields it changes and the decision.
create table security.four_eyes (
  tenant_id uuid primary key,
  enabled boolean not null default false,
  updated_at timestamptz not null,
  updated_by uuid null
);
select platform.enable_tenant_rls('security.four_eyes');
grant select, insert, update on security.four_eyes to tb_app;

create table security.pending_change (
  tenant_id uuid not null,
  id uuid not null,
  kind text not null,
  request jsonb not null,
  fields text[] not null,
  status text not null check (status in ('pending', 'approved', 'rejected', 'withdrawn', 'expired')),
  requested_by uuid not null,
  requested_at timestamptz not null,
  expires_at timestamptz not null,
  decided_by uuid null,
  decided_at timestamptz null,
  reason text null check (reason is null or length(reason) <= 500),
  primary key (tenant_id, id)
);
-- One pending change per kind.
create unique index pending_change_one_per_kind on security.pending_change (tenant_id, kind) where status = 'pending';
create index pending_change_recent on security.pending_change (tenant_id, requested_at desc);
select platform.enable_tenant_rls('security.pending_change');
grant select, insert, update on security.pending_change to tb_app;
