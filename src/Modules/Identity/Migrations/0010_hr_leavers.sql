-- Spec 019 FR-008: people whose access ended because HR says they left, so a rehire can reopen exactly those
-- accounts when the tenant allows it, and no account an administrator disabled. Tenant-owned, forced RLS.
create table identity.hr_leaver (
  tenant_id uuid not null,
  user_id uuid not null,
  record_id text not null,
  disabled_at timestamptz not null,
  primary key (tenant_id, user_id)
);
select platform.enable_tenant_rls('identity.hr_leaver');
grant select, insert, update, delete on identity.hr_leaver to tb_app;
