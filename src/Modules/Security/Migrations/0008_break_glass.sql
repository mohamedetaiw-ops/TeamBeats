-- Spec 026 FR-020 and spec 001: platform operator access to a tenant only through a break-glass session. Rows are
-- written under the subject tenant's context, so forced row-level security applies as for any tenant table.
create table security.break_glass_session (
  tenant_id uuid not null,
  id uuid not null,
  operator_id uuid not null,
  reason text not null check (length(reason) between 10 and 500),
  opened_at timestamptz not null,
  expires_at timestamptz not null,
  ended_at timestamptz null,
  actions int not null default 0,
  primary key (tenant_id, id),
  check (expires_at > opened_at)
);
create index break_glass_session_open on security.break_glass_session (tenant_id, operator_id) where ended_at is null;
select platform.enable_tenant_rls('security.break_glass_session');
grant select, insert, update on security.break_glass_session to tb_app;
