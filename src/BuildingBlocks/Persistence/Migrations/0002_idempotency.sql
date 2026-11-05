-- Idempotency keys for retryable create operations (constitution XVI). Scope includes the caller identity,
-- and for tenant-scoped operations the tenant ID, so keys never collide across tenants or callers.
create table platform.idempotency_key (
  scope text not null,
  key text not null,
  request_hash text not null,
  status_code int not null,
  response jsonb not null,
  created_at timestamptz not null default now(),
  primary key (scope, key)
);
grant select, insert on platform.idempotency_key to tb_app;
